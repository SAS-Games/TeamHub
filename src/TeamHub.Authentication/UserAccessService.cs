using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace TeamHub.Authentication;

internal sealed class UserAccessService(
    IDbContextFactory<AccessControlDbContext> contextFactory,
    IOptions<BootstrapAdminOptions> bootstrapAdminOptions) : IUserAccessService
{
    private const string BootstrapCompleteKey = "BootstrapUsersImported";
    private const string LegacyStudioSupportModule = "Studio Jira Tickets";
    private const string CustomTeamTabModulePrefix = "Team Tab: ";
    private readonly PasswordHasher<AuthorizedUserEntity> passwordHasher = new();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureGidSchemaAsync(context, cancellationToken);
        await EnsureUserPermissionSchemaAsync(context, cancellationToken);
        await MigrateStudioSupportPermissionsAsync(context, cancellationToken);
        await SeedPermissionsAsync(context, TeamHubModules.All, cancellationToken);

        if (await context.Settings.AnyAsync(item => item.Key == BootstrapCompleteKey, cancellationToken)) return;
        if (await context.AuthorizedUsers.AnyAsync(
                item => item.IsActive && item.UserType == TeamHubUserTypes.Admin,
                cancellationToken))
        {
            context.Settings.Add(new AccessSettingEntity { Key = BootstrapCompleteKey, Value = DateTimeOffset.UtcNow.ToString("O") });
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        var bootstrap = bootstrapAdminOptions.Value;
        if (string.IsNullOrWhiteSpace(bootstrap.UserId) || string.IsNullOrEmpty(bootstrap.Password)) return;
        if (bootstrap.Password.Length < 8)
        {
            throw new InvalidOperationException("TEAMHUB_BOOTSTRAP_ADMIN_PASSWORD must contain at least 8 characters.");
        }

        var now = DateTime.UtcNow;
        var userId = bootstrap.UserId.Trim();
        var normalizedUserId = NormalizeUserId(userId);
        var entity = await context.AuthorizedUsers
            .SingleOrDefaultAsync(item => item.NormalizedUserId == normalizedUserId, cancellationToken);
        entity ??= new AuthorizedUserEntity
        {
            Id = Guid.NewGuid(),
            CreatedAtUtc = now,
        };
        if (context.Entry(entity).State == EntityState.Detached) context.AuthorizedUsers.Add(entity);
        entity.UserId = userId;
        entity.NormalizedUserId = normalizedUserId;
        entity.DisplayName = string.IsNullOrWhiteSpace(bootstrap.DisplayName) ? userId : bootstrap.DisplayName.Trim();
        entity.UserType = TeamHubUserTypes.Admin;
        entity.IsActive = true;
        entity.IsRegistered = true;
        entity.UpdatedAtUtc = now;
        entity.PasswordHash = passwordHasher.HashPassword(entity, bootstrap.Password);
        context.Settings.Add(new AccessSettingEntity { Key = BootstrapCompleteKey, Value = DateTimeOffset.UtcNow.ToString("O") });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task EnsureModulesAsync(IReadOnlyCollection<string> modules, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        await SeedPermissionsAsync(context, modules, cancellationToken);
    }

    public async Task RemoveModulesAsync(IReadOnlyCollection<string> modules, CancellationToken cancellationToken = default)
    {
        var removable = modules
            .Where(module => !string.IsNullOrWhiteSpace(module) && !TeamHubModules.All.Contains(module.Trim()))
            .Select(module => module.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (removable.Count == 0) return;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.UserModulePermissions
            .Where(item => removable.Contains(item.Module))
            .ExecuteDeleteAsync(cancellationToken);
        await context.ModulePermissions
            .Where(item => removable.Contains(item.Module))
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<AuthorizedUserRecord?> ValidateCredentialsAsync(string identifier, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrEmpty(password)) return null;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await FindByIdentifierAsync(context, identifier, activeOnly: true, asNoTracking: true, cancellationToken);
        if (entity?.IsRegistered != true || string.IsNullOrWhiteSpace(entity.PasswordHash)) return null;

        var verification = passwordHasher.VerifyHashedPassword(entity, entity.PasswordHash, password);
        return verification == PasswordVerificationResult.Failed ? null : ToRecord(entity);
    }

    public async Task<AuthorizedUserRecord?> FindActiveUserAsync(string identifier, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return null;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await FindByIdentifierAsync(context, identifier, activeOnly: true, asNoTracking: true, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<RegistrationResult> RegisterAsync(RegisterAuthorizedUserRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Identifier)) return new(false, "Enter your organization email, user ID, or GID.");
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < 8) return new(false, "Password must contain at least 8 characters.");

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await FindByIdentifierAsync(context, request.Identifier, activeOnly: false, asNoTracking: false, cancellationToken);
        if (entity is null || !entity.IsActive) return new(false, "This user has not been authorized by a TeamHub administrator.");
        if (entity.UserType != TeamHubUserTypes.Registered) return new(false, "This account is managed by an administrator or organization sign-in.");
        if (entity.IsRegistered) return new(false, "This account is already registered. Sign in instead.");

        entity.PasswordHash = passwordHasher.HashPassword(entity, request.Password);
        entity.IsRegistered = true;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return new(true, "Registration complete. You can now sign in.");
    }

    public async Task<IReadOnlyList<AuthorizedUserRecord>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return (await context.AuthorizedUsers.AsNoTracking()
                .OrderBy(item => item.DisplayName)
                .ThenBy(item => item.UserId)
                .ToListAsync(cancellationToken))
            .Select(ToRecord)
            .ToList();
    }

    public async Task<AuthorizedUserRecord?> GetUserAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.AuthorizedUsers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<AuthorizedUserRecord> SaveUserAsync(SaveAuthorizedUserRequest request, string? actorUserId, CancellationToken cancellationToken = default)
    {
        var userId = request.UserId?.Trim();
        var gid = NormalizeGid(request.Gid);
        var displayName = request.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("Organization email or user ID is required.");
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Display name is required.");
        if (request.TemporaryPassword is { Length: > 0 and < 8 }) throw new ArgumentException("Temporary password must contain at least 8 characters.");
        var userType = TeamHubUserTypes.Normalize(request.UserType);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var normalized = NormalizeUserId(userId);
        var normalizedGid = gid is null ? null : NormalizeUserId(gid);
        var duplicate = await context.AuthorizedUsers.AnyAsync(
            item => item.NormalizedUserId == normalized && (!request.Id.HasValue || item.Id != request.Id.Value), cancellationToken);
        if (duplicate) throw new InvalidOperationException("That organization email or user ID is already authorized.");
        if (gid is not null && await context.AuthorizedUsers.AnyAsync(
                item => item.Gid == gid && (!request.Id.HasValue || item.Id != request.Id.Value), cancellationToken))
        {
            throw new InvalidOperationException("That GID is already assigned to another authorized user.");
        }
        if (gid is not null && await context.AuthorizedUsers.AnyAsync(
                item => item.NormalizedUserId == normalizedGid
                    && (!request.Id.HasValue || item.Id != request.Id.Value), cancellationToken))
        {
            throw new InvalidOperationException("That GID conflicts with another user's email or user ID.");
        }
        if (IsNumericIdentifier(userId) && await context.AuthorizedUsers.AnyAsync(
                item => item.Gid == userId && (!request.Id.HasValue || item.Id != request.Id.Value), cancellationToken))
        {
            throw new InvalidOperationException("That user ID conflicts with another user's GID.");
        }

        var entity = request.Id.HasValue
            ? await context.AuthorizedUsers.SingleOrDefaultAsync(item => item.Id == request.Id.Value, cancellationToken)
            : null;
        if (request.Id.HasValue && entity is null) throw new KeyNotFoundException("Authorized user was not found.");
        if (entity is not null && entity.UserType == TeamHubUserTypes.Admin
            && (userType != TeamHubUserTypes.Admin || !request.IsActive))
        {
            await EnsureAnotherActiveAdminAsync(context, entity.Id, cancellationToken);
        }

        var now = DateTime.UtcNow;
        entity ??= new AuthorizedUserEntity { Id = Guid.NewGuid(), CreatedAtUtc = now };
        if (context.Entry(entity).State == EntityState.Detached) context.AuthorizedUsers.Add(entity);
        entity.UserId = userId;
        entity.NormalizedUserId = normalized;
        entity.Gid = gid;
        entity.DisplayName = displayName;
        entity.UserType = userType;
        entity.IsActive = request.IsActive;
        entity.UpdatedAtUtc = now;
        if (!string.IsNullOrEmpty(request.TemporaryPassword))
        {
            entity.PasswordHash = passwordHasher.HashPassword(entity, request.TemporaryPassword);
            entity.IsRegistered = true;
        }

        await context.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task SetUserActiveAsync(Guid id, bool isActive, string? actorUserId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.AuthorizedUsers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Authorized user was not found.");
        if (!isActive && entity.UserType == TeamHubUserTypes.Admin)
        {
            await EnsureAnotherActiveAdminAsync(context, id, cancellationToken);
        }
        if (!isActive && string.Equals(entity.UserId, actorUserId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("You cannot deactivate your own account.");
        }
        entity.IsActive = isActive;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteUserAsync(Guid id, string? actorUserId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.AuthorizedUsers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Authorized user was not found.");
        if (string.Equals(entity.UserId, actorUserId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("You cannot remove your own account.");
        }
        if (entity.UserType == TeamHubUserTypes.Admin)
        {
            await EnsureAnotherActiveAdminAsync(context, id, cancellationToken);
        }
        context.AuthorizedUsers.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ModulePermissionRecord>> ListPermissionsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return (await context.ModulePermissions.AsNoTracking().ToListAsync(cancellationToken))
            .OrderBy(item => ModuleOrder(item.Module).Group)
            .ThenBy(item => ModuleOrder(item.Module).Index)
            .ThenBy(item => item.Module)
            .ThenBy(item => Array.IndexOf(TeamHubUserTypes.All.ToArray(), item.UserType))
            .Select(item => new ModulePermissionRecord(item.Module, item.UserType, item.AccessLevel))
            .ToList();
    }

    public async Task SetPermissionsAsync(IReadOnlyCollection<ModulePermissionRecord> permissions, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var stored = await context.ModulePermissions.ToListAsync(cancellationToken);
        var byKey = stored.ToDictionary(item => item.Module + "|" + item.UserType, StringComparer.Ordinal);
        foreach (var input in permissions)
        {
            if (!TeamHubUserTypes.All.Contains(input.UserType)) continue;
            if (!byKey.TryGetValue(input.Module + "|" + input.UserType, out var entity)) continue;
            var level = NormalizePermission(input.Module, input.UserType, input.AccessLevel);
            entity.AccessLevel = level;
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserModulePermissionRecord>> ListUserPermissionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.UserModulePermissions.AsNoTracking()
            .OrderBy(item => item.Module)
            .ThenBy(item => item.AuthorizedUserId)
            .Select(item => new UserModulePermissionRecord(
                item.AuthorizedUserId,
                item.Module,
                item.AccessLevel))
            .ToListAsync(cancellationToken);
    }

    public async Task SetUserPermissionsAsync(
        IReadOnlyCollection<UserModulePermissionRecord> permissions,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var validUsers = await context.AuthorizedUsers.AsNoTracking()
            .Where(item => item.IsActive && item.UserType != TeamHubUserTypes.Admin)
            .Select(item => item.Id)
            .ToHashSetAsync(cancellationToken);
        var validModules = await context.ModulePermissions.AsNoTracking()
            .Where(item => item.Module.StartsWith(CustomTeamTabModulePrefix))
            .Select(item => item.Module)
            .Distinct()
            .ToHashSetAsync(cancellationToken);
        var stored = await context.UserModulePermissions.ToListAsync(cancellationToken);
        var byKey = stored.ToDictionary(
            item => item.AuthorizedUserId + "|" + item.Module,
            StringComparer.Ordinal);

        foreach (var input in permissions)
        {
            if (!validUsers.Contains(input.AuthorizedUserId)
                || !validModules.Contains(input.Module))
                continue;
            var key = input.AuthorizedUserId + "|" + input.Module;
            if (!input.AccessLevel.HasValue)
            {
                if (byKey.Remove(key, out var inherited)) context.UserModulePermissions.Remove(inherited);
                continue;
            }

            var level = NormalizeUserOverride(input.AccessLevel.Value);
            if (!byKey.TryGetValue(key, out var entity))
            {
                entity = new UserModulePermissionEntity
                {
                    AuthorizedUserId = input.AuthorizedUserId,
                    Module = input.Module
                };
                context.UserModulePermissions.Add(entity);
                byKey[key] = entity;
            }
            entity.AccessLevel = level;
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<AccessLevel> GetAccessLevelAsync(string module, string userType, CancellationToken cancellationToken = default)
        => await GetAccessLevelAsync(module, userType, null, cancellationToken);

    public async Task<AccessLevel> GetAccessLevelAsync(
        string module,
        string userType,
        Guid? authorizedUserId,
        CancellationToken cancellationToken = default)
    {
        if (userType == TeamHubUserTypes.Admin) return AccessLevel.FullAccess;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (authorizedUserId.HasValue)
        {
            var userOverride = await context.UserModulePermissions.AsNoTracking()
                .Where(item => item.AuthorizedUserId == authorizedUserId.Value && item.Module == module)
                .Select(item => (AccessLevel?)item.AccessLevel)
                .SingleOrDefaultAsync(cancellationToken);
            if (userOverride.HasValue) return userOverride.Value;
        }
        return await context.ModulePermissions.AsNoTracking()
            .Where(item => item.Module == module && item.UserType == userType)
            .Select(item => (AccessLevel?)item.AccessLevel)
            .SingleOrDefaultAsync(cancellationToken) ?? AccessLevel.NoAccess;
    }

    private static async Task EnsureUserPermissionSchemaAsync(
        AccessControlDbContext context,
        CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS UserModulePermissions (
                Id INTEGER NOT NULL CONSTRAINT PK_UserModulePermissions PRIMARY KEY AUTOINCREMENT,
                AuthorizedUserId TEXT NOT NULL,
                Module TEXT NOT NULL,
                AccessLevel INTEGER NOT NULL,
                CONSTRAINT FK_UserModulePermissions_AuthorizedUsers_AuthorizedUserId
                    FOREIGN KEY (AuthorizedUserId) REFERENCES AuthorizedUsers (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_UserModulePermissions_AuthorizedUserId_Module
                ON UserModulePermissions (AuthorizedUserId, Module);
            """, cancellationToken);
    }

    private static async Task SeedPermissionsAsync(
        AccessControlDbContext context,
        IEnumerable<string> modules,
        CancellationToken cancellationToken)
    {
        var existing = await context.ModulePermissions
            .Select(item => item.Module + "|" + item.UserType)
            .ToHashSetAsync(cancellationToken);
        foreach (var module in modules
                     .Where(IsValidModule)
                     .Select(item => item.Trim())
                     .Distinct(StringComparer.Ordinal))
        {
            foreach (var userType in TeamHubUserTypes.All)
            {
                if (existing.Contains(module + "|" + userType)) continue;
                context.ModulePermissions.Add(new ModulePermissionEntity
                {
                    Module = module,
                    UserType = userType,
                    AccessLevel = DefaultPermission(module, userType)
                });
            }
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task MigrateStudioSupportPermissionsAsync(
        AccessControlDbContext context,
        CancellationToken cancellationToken)
    {
        var legacyPermissions = await context.ModulePermissions
            .Where(item => item.Module == LegacyStudioSupportModule)
            .ToListAsync(cancellationToken);
        if (legacyPermissions.Count == 0) return;

        var currentPermissions = await context.ModulePermissions
            .Where(item => item.Module == TeamHubModules.StudioSupport)
            .ToDictionaryAsync(item => item.UserType, StringComparer.Ordinal, cancellationToken);
        foreach (var legacy in legacyPermissions)
        {
            if (currentPermissions.TryGetValue(legacy.UserType, out var current))
            {
                current.AccessLevel = legacy.AccessLevel;
                context.ModulePermissions.Remove(legacy);
            }
            else
            {
                legacy.Module = TeamHubModules.StudioSupport;
            }
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    private static bool IsValidModule(string? module) =>
        !string.IsNullOrWhiteSpace(module) && module.Trim().Length <= 100;

    private static (int Group, int Index) ModuleOrder(string module)
    {
        var index = Array.IndexOf(TeamHubModules.All.ToArray(), module);
        return index >= 0 ? (0, index) : (1, int.MaxValue);
    }

    private static AccessLevel DefaultPermission(string module, string userType)
    {
        if (userType == TeamHubUserTypes.Admin) return AccessLevel.FullAccess;
        if (module is TeamHubModules.UserManagement or TeamHubModules.AccessManagement or TeamHubModules.Administration)
            return AccessLevel.NoAccess;
        return (module, userType) switch
        {
            (TeamHubModules.Home or TeamHubModules.UsefulLinks, _) => AccessLevel.ReadOnly,
            (TeamHubModules.Team, TeamHubUserTypes.Registered) => AccessLevel.ReadOnly,
            (TeamHubModules.Team, TeamHubUserTypes.Privileged) => AccessLevel.Edit,
            (TeamHubModules.WorkCenter, TeamHubUserTypes.Registered) => AccessLevel.Edit,
            (TeamHubModules.WorkCenter, TeamHubUserTypes.Privileged) => AccessLevel.FullAccess,
            (TeamHubModules.Milestones, TeamHubUserTypes.Registered) => AccessLevel.ReadOnly,
            (TeamHubModules.Milestones, TeamHubUserTypes.Privileged) => AccessLevel.Edit,
            (TeamHubModules.FlowDesigner, TeamHubUserTypes.Registered) => AccessLevel.Edit,
            (TeamHubModules.FlowDesigner, TeamHubUserTypes.Privileged) => AccessLevel.FullAccess,
            (TeamHubModules.StudioConfiguration, TeamHubUserTypes.Registered) => AccessLevel.ReadOnly,
            (TeamHubModules.StudioConfiguration, TeamHubUserTypes.Privileged) => AccessLevel.Edit,
            (TeamHubModules.StudioSupport, TeamHubUserTypes.Registered or TeamHubUserTypes.Privileged) => AccessLevel.ReadOnly,
            (TeamHubModules.AtlassianConnection, TeamHubUserTypes.Registered or TeamHubUserTypes.Privileged) => AccessLevel.Edit,
            _ => AccessLevel.NoAccess
        };
    }

    private static AccessLevel NormalizePermission(string module, string userType, AccessLevel requested)
    {
        if (userType == TeamHubUserTypes.Admin) return AccessLevel.FullAccess;
        if (module is TeamHubModules.UserManagement or TeamHubModules.AccessManagement)
            return AccessLevel.NoAccess;
        return Enum.IsDefined(requested) ? requested : AccessLevel.NoAccess;
    }

    private static AccessLevel NormalizeUserOverride(AccessLevel requested) => requested switch
    {
        AccessLevel.NoAccess or AccessLevel.ReadOnly or AccessLevel.Edit => requested,
        _ => throw new ArgumentException("Per-user custom tab access must be No Access, Read Only, or Edit.")
    };

    private static async Task EnsureAnotherActiveAdminAsync(AccessControlDbContext context, Guid excludedId, CancellationToken cancellationToken)
    {
        if (!await context.AuthorizedUsers.AnyAsync(
                item => item.Id != excludedId && item.IsActive && item.UserType == TeamHubUserTypes.Admin,
                cancellationToken))
        {
            throw new InvalidOperationException("TeamHub must keep at least one active administrator.");
        }
    }

    private static string NormalizeUserId(string userId) => userId.Trim().ToUpperInvariant();

    private static string? NormalizeGid(string? gid)
    {
        if (string.IsNullOrWhiteSpace(gid)) return null;
        var normalized = gid.Trim();
        if (normalized.Length > 32 || !IsNumericIdentifier(normalized))
        {
            throw new ArgumentException("GID must contain numbers only and be no longer than 32 digits.");
        }
        return normalized;
    }

    private static bool IsNumericIdentifier(string value) =>
        value.Length > 0 && value.All(character => character is >= '0' and <= '9');

    private static async Task<AuthorizedUserEntity?> FindByIdentifierAsync(
        AccessControlDbContext context,
        string identifier,
        bool activeOnly,
        bool asNoTracking,
        CancellationToken cancellationToken)
    {
        var trimmed = identifier.Trim();
        var normalizedUserId = NormalizeUserId(trimmed);
        var gid = IsNumericIdentifier(trimmed) && trimmed.Length <= 32 ? trimmed : null;
        IQueryable<AuthorizedUserEntity> query = context.AuthorizedUsers;
        if (asNoTracking) query = query.AsNoTracking();
        if (activeOnly) query = query.Where(item => item.IsActive);
        return await query.SingleOrDefaultAsync(
            item => item.NormalizedUserId == normalizedUserId || (gid != null && item.Gid == gid),
            cancellationToken);
    }

    private static async Task EnsureGidSchemaAsync(AccessControlDbContext context, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        var shouldClose = connection.State == System.Data.ConnectionState.Closed;
        if (shouldClose) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var columnCheck = connection.CreateCommand();
            columnCheck.CommandText = "SELECT COUNT(*) FROM pragma_table_info('AuthorizedUsers') WHERE name = 'Gid';";
            var gidColumnExists = Convert.ToInt32(await columnCheck.ExecuteScalarAsync(cancellationToken)) > 0;
            if (!gidColumnExists)
            {
                await using var addColumn = connection.CreateCommand();
                addColumn.CommandText = "ALTER TABLE AuthorizedUsers ADD COLUMN Gid TEXT NULL;";
                await addColumn.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var addIndex = connection.CreateCommand();
            addIndex.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_AuthorizedUsers_Gid ON AuthorizedUsers (Gid);";
            await addIndex.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    private static AuthorizedUserRecord ToRecord(AuthorizedUserEntity entity) => new(
        entity.Id,
        entity.UserId,
        entity.Gid,
        entity.DisplayName,
        entity.UserType,
        entity.IsActive,
        entity.IsRegistered,
        new DateTimeOffset(DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(entity.UpdatedAtUtc, DateTimeKind.Utc)));
}
