using FluentAssertions;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.Authentication;
using TeamHub.Web.AccessControl;
using TeamHub.Web.FlowDesigner;

namespace TeamHub.Tests;

public sealed class AccessControlTests
{
    [Fact]
    public async Task BootstrapUser_IsImportedAndAuthenticatedFromAuthorizedList()
    {
        await using var host = await AccessTestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAccessService>();

        var authenticated = await users.ValidateCredentialsAsync("admin@example.com", "bootstrap123");
        await users.InitializeAsync();

        authenticated.Should().NotBeNull();
        authenticated!.UserType.Should().Be(TeamHubUserTypes.Admin);
        (await users.ListUsersAsync()).Should().ContainSingle(item => item.UserId == "admin@example.com");
    }

    [Fact]
    public async Task Registration_RequiresAnActiveAuthorizedRegisteredUser()
    {
        await using var host = await AccessTestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAccessService>();
        var authorized = await users.SaveUserAsync(new SaveAuthorizedUserRequest(
            null, "person@example.com", null, "Person", TeamHubUserTypes.Registered, true, null), "admin@example.com");

        (await users.RegisterAsync(new("unknown@example.com", "password123"))).Success.Should().BeFalse();
        (await users.ValidateCredentialsAsync("person@example.com", "password123")).Should().BeNull();
        (await users.RegisterAsync(new("person@example.com", "password123"))).Success.Should().BeTrue();
        (await users.ValidateCredentialsAsync("person@example.com", "password123")).Should().NotBeNull();

        await users.SetUserActiveAsync(authorized.Id, false, "admin@example.com");
        (await users.ValidateCredentialsAsync("person@example.com", "password123")).Should().BeNull();
    }

    [Fact]
    public async Task RegistrationAndLogin_AcceptTheAuthorizedUsersGid()
    {
        await using var host = await AccessTestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAccessService>();
        var authorized = await users.SaveUserAsync(new SaveAuthorizedUserRequest(
            null, "gid.user@example.com", "00123456", "GID User", TeamHubUserTypes.Registered, true, null), "admin@example.com");

        var registration = await users.RegisterAsync(new("00123456", "password123"));
        var byGid = await users.ValidateCredentialsAsync("00123456", "password123");
        var byEmail = await users.ValidateCredentialsAsync("gid.user@example.com", "password123");

        registration.Success.Should().BeTrue();
        byGid.Should().NotBeNull();
        byGid!.Id.Should().Be(authorized.Id);
        byGid.UserId.Should().Be("gid.user@example.com");
        byGid.Gid.Should().Be("00123456");
        byEmail!.Id.Should().Be(authorized.Id);
    }

    [Fact]
    public async Task SaveUser_RequiresANumericUniqueGid()
    {
        await using var host = await AccessTestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAccessService>();
        await users.SaveUserAsync(new SaveAuthorizedUserRequest(
            null, "first@example.com", "123456", "First User", TeamHubUserTypes.Registered, true, null), "admin@example.com");

        var invalid = async () => await users.SaveUserAsync(new SaveAuthorizedUserRequest(
            null, "invalid@example.com", "12A456", "Invalid User", TeamHubUserTypes.Registered, true, null), "admin@example.com");
        var duplicate = async () => await users.SaveUserAsync(new SaveAuthorizedUserRequest(
            null, "duplicate@example.com", "123456", "Duplicate User", TeamHubUserTypes.Registered, true, null), "admin@example.com");

        await invalid.Should().ThrowAsync<ArgumentException>().WithMessage("GID must contain numbers only*");
        await duplicate.Should().ThrowAsync<InvalidOperationException>().WithMessage("That GID is already assigned*");
    }

    [Fact]
    public async Task PermissionMatrix_UsesDefaultsAndPersistsAdminChanges()
    {
        await using var host = await AccessTestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAccessService>();

        (await users.GetAccessLevelAsync(TeamHubModules.Home, TeamHubUserTypes.Guest)).Should().Be(AccessLevel.ReadOnly);
        (await users.GetAccessLevelAsync(TeamHubModules.FlowDesigner, TeamHubUserTypes.Registered)).Should().Be(AccessLevel.Edit);
        (await users.GetAccessLevelAsync(TeamHubModules.AtlassianConnection, TeamHubUserTypes.Registered)).Should().Be(AccessLevel.Edit);
        (await users.GetAccessLevelAsync(TeamHubModules.StudioSupport, TeamHubUserTypes.Privileged)).Should().Be(AccessLevel.ReadOnly);

        await users.SetPermissionsAsync([
            new(TeamHubModules.Milestones, TeamHubUserTypes.Registered, AccessLevel.Create),
            new(TeamHubModules.UserManagement, TeamHubUserTypes.Privileged, AccessLevel.FullAccess),
            new(TeamHubModules.Home, TeamHubUserTypes.Admin, AccessLevel.NoAccess)
        ]);

        (await users.GetAccessLevelAsync(TeamHubModules.Milestones, TeamHubUserTypes.Registered)).Should().Be(AccessLevel.Create);
        (await users.GetAccessLevelAsync(TeamHubModules.UserManagement, TeamHubUserTypes.Privileged)).Should().Be(AccessLevel.NoAccess);
        (await users.GetAccessLevelAsync(TeamHubModules.Home, TeamHubUserTypes.Admin)).Should().Be(AccessLevel.FullAccess);
    }

    [Fact]
    public async Task InitializeAsync_MigratesLegacyStudioJiraPermissionsToStudioSupport()
    {
        await using var host = await AccessTestHost.CreateAsync();
        await using (var connection = new SqliteConnection($"Data Source={host.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM ModulePermissions WHERE Module = 'Studio Support' AND UserType = 'Registered';
                INSERT INTO ModulePermissions (Module, UserType, AccessLevel)
                VALUES ('Studio Jira Tickets', 'Registered', 3);
                """;
            await command.ExecuteNonQueryAsync();
        }

        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAccessService>();
        await users.InitializeAsync();

        (await users.GetAccessLevelAsync(TeamHubModules.StudioSupport, TeamHubUserTypes.Registered)).Should().Be(AccessLevel.Edit);
        (await users.ListPermissionsAsync()).Should().NotContain(item => item.Module == "Studio Jira Tickets");
    }

    [Fact]
    public async Task DiscoveredModule_IsAddedWithFailClosedDefaultsAndCanBeConfigured()
    {
        await using var host = await AccessTestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAccessService>();

        await users.EnsureModulesAsync(["Reports"]);

        (await users.GetAccessLevelAsync("Reports", TeamHubUserTypes.Guest)).Should().Be(AccessLevel.NoAccess);
        (await users.GetAccessLevelAsync("Reports", TeamHubUserTypes.Registered)).Should().Be(AccessLevel.NoAccess);
        (await users.GetAccessLevelAsync("Reports", TeamHubUserTypes.Privileged)).Should().Be(AccessLevel.NoAccess);
        (await users.GetAccessLevelAsync("Reports", TeamHubUserTypes.Admin)).Should().Be(AccessLevel.FullAccess);

        await users.SetPermissionsAsync([new("Reports", TeamHubUserTypes.Registered, AccessLevel.ReadOnly)]);
        (await users.GetAccessLevelAsync("Reports", TeamHubUserTypes.Registered)).Should().Be(AccessLevel.ReadOnly);
    }

    [Fact]
    public async Task CustomTab_PerUserOverrideSupersedesUserTypeAccessAndCanReturnToInheritance()
    {
        await using var host = await AccessTestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAccessService>();
        const string module = "Team Tab: restricted-workbook";
        await users.EnsureModulesAsync([module]);
        var allowed = await users.SaveUserAsync(new SaveAuthorizedUserRequest(
            null, "allowed@example.com", null, "Allowed User", TeamHubUserTypes.Registered, true, "password123"), "admin@example.com");
        var blocked = await users.SaveUserAsync(new SaveAuthorizedUserRequest(
            null, "blocked@example.com", null, "Blocked User", TeamHubUserTypes.Registered, true, "password123"), "admin@example.com");

        await users.SetUserPermissionsAsync([
            new(allowed.Id, module, AccessLevel.ReadOnly),
            new(blocked.Id, module, AccessLevel.NoAccess)
        ]);

        (await users.GetAccessLevelAsync(module, TeamHubUserTypes.Registered, allowed.Id)).Should().Be(AccessLevel.ReadOnly);
        (await users.GetAccessLevelAsync(module, TeamHubUserTypes.Registered, blocked.Id)).Should().Be(AccessLevel.NoAccess);
        (await users.GetAccessLevelAsync(module, TeamHubUserTypes.Registered)).Should().Be(AccessLevel.NoAccess);

        await users.SetPermissionsAsync([new(module, TeamHubUserTypes.Registered, AccessLevel.Edit)]);
        (await users.GetAccessLevelAsync(module, TeamHubUserTypes.Registered, allowed.Id)).Should().Be(AccessLevel.ReadOnly);
        (await users.GetAccessLevelAsync(module, TeamHubUserTypes.Registered, blocked.Id)).Should().Be(AccessLevel.NoAccess);

        await users.SetUserPermissionsAsync([new(allowed.Id, module, null)]);
        (await users.GetAccessLevelAsync(module, TeamHubUserTypes.Registered, allowed.Id)).Should().Be(AccessLevel.Edit);
        (await users.ListUserPermissionsAsync()).Should().ContainSingle(item =>
            item.AuthorizedUserId == blocked.Id && item.Module == module);
    }

    [Fact]
    public async Task InitializeAsync_AddsPerUserPermissionSchemaToAnExistingAccessDatabase()
    {
        await using var host = await AccessTestHost.CreateAsync();
        await using (var connection = new SqliteConnection($"Data Source={host.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE UserModulePermissions;";
            await drop.ExecuteNonQueryAsync();
        }

        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IUserAccessService>().InitializeAsync();

        await using var verification = new SqliteConnection($"Data Source={host.DatabasePath};Pooling=False");
        await verification.OpenAsync();
        await using var command = verification.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'UserModulePermissions';";
        Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(1);
    }

    [Fact]
    public async Task InitializeAsync_AddsGidSchemaToAnExistingAccessDatabase()
    {
        await using var host = await AccessTestHost.CreateAsync();
        await using (var connection = new SqliteConnection($"Data Source={host.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP INDEX IX_AuthorizedUsers_Gid; ALTER TABLE AuthorizedUsers DROP COLUMN Gid;";
            await command.ExecuteNonQueryAsync();
        }

        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IUserAccessService>().InitializeAsync();

        await using var verification = new SqliteConnection($"Data Source={host.DatabasePath};Pooling=False");
        await verification.OpenAsync();
        await using var column = verification.CreateCommand();
        column.CommandText = "SELECT COUNT(*) FROM pragma_table_info('AuthorizedUsers') WHERE name = 'Gid';";
        Convert.ToInt32(await column.ExecuteScalarAsync()).Should().Be(1);
        await using var index = verification.CreateCommand();
        index.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_AuthorizedUsers_Gid';";
        Convert.ToInt32(await index.ExecuteScalarAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RemoveModules_DeletesDynamicPermissionsButProtectsBuiltInModules()
    {
        await using var host = await AccessTestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAccessService>();
        const string module = "Team Tab: temporary";

        await users.EnsureModulesAsync([module]);
        await users.SetPermissionsAsync([new(module, TeamHubUserTypes.Registered, AccessLevel.Edit)]);
        var user = await users.SaveUserAsync(new SaveAuthorizedUserRequest(
            null, "temporary@example.com", null, "Temporary User", TeamHubUserTypes.Registered, true, "password123"), "admin@example.com");
        await users.SetUserPermissionsAsync([new(user.Id, module, AccessLevel.ReadOnly)]);

        await users.RemoveModulesAsync([module, TeamHubModules.Team]);

        (await users.GetAccessLevelAsync(module, TeamHubUserTypes.Registered)).Should().Be(AccessLevel.NoAccess);
        (await users.GetAccessLevelAsync(TeamHubModules.Team, TeamHubUserTypes.Registered)).Should().Be(AccessLevel.ReadOnly);
        (await users.ListPermissionsAsync()).Should().NotContain(item => item.Module == module);
        (await users.ListUserPermissionsAsync()).Should().NotContain(item => item.Module == module);
    }
    [Theory]
    [InlineData("/Reports/Index", "Reports")]
    [InlineData("/ReleaseNotes", "Release Notes")]
    [InlineData("/Administration/Audit", "Administration")]
    [InlineData("/Studio/JiraTickets", TeamHubModules.StudioSupport)]
    [InlineData("/Studio/WeeklyUpdates", TeamHubModules.StudioSupport)]
    [InlineData("/Team/Custom/support-metrics", "Team Tab: support-metrics")]
    [InlineData("/Register", null)]
    [InlineData("/Logout", null)]
    public void PageDiscovery_UsesFirstFolderAndPreservesSpecialModules(string pagePath, string? expected)
    {
        TeamHubModuleCatalog.ResolvePage(pagePath).Should().Be(expected);
    }

    [Fact]
    public void ModuleCatalog_DiscoversRazorPageEndpointMetadata()
    {
        var endpoint = new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/Reports"),
            0);
        endpoint.Metadata.Add(new PageActionDescriptor { ViewEnginePath = "/Reports/Index" });
        var catalog = new TeamHubModuleCatalog();

        catalog.Discover([new DefaultEndpointDataSource(endpoint.Build())]);

        catalog.Modules.Should().Contain("Reports");
    }

    [Theory]
    [InlineData("GET", "/Milestones", "", AccessLevel.ReadOnly)]
    [InlineData("GET", "/Studio/Configuration", "", AccessLevel.ReadOnly)]
    [InlineData("GET", "/Studio/Configuration", "?create=true", AccessLevel.Create)]
    [InlineData("POST", "/Studio/Configuration", "?handler=Create", AccessLevel.Create)]
    [InlineData("GET", "/Studio/Configuration", "?studioId=1", AccessLevel.Edit)]
    [InlineData("POST", "/Studio/Configuration", "?handler=Edit", AccessLevel.Edit)]
    [InlineData("GET", "/Flows/New", "", AccessLevel.Create)]
    [InlineData("DELETE", "/api/flows/00000000-0000-0000-0000-000000000001", "", AccessLevel.Create)]
    [InlineData("POST", "/Flows", "?handler=Delete", AccessLevel.Edit)]
    [InlineData("POST", "/Flows", "?handler=DeletePublished", AccessLevel.FullAccess)]
    [InlineData("POST", "/Studio/Configuration", "?handler=Delete", AccessLevel.Delete)]
    [InlineData("GET", "/Flows/Review", "", AccessLevel.FullAccess)]
    [InlineData("GET", "/api/flows/publication-requests/id/snapshot", "", AccessLevel.FullAccess)]
    [InlineData("POST", "/api/flows/id/publication-requests", "", AccessLevel.Edit)]
    [InlineData("POST", "/Team/Custom/support-metrics", "?handler=SaveRow", AccessLevel.Edit)]
    [InlineData("GET", "/api/flows/published/id", "", AccessLevel.ReadOnly)]
    [InlineData("PUT", "/api/flows/published/id", "", AccessLevel.FullAccess)]
    public void RouteAccess_MapsOperationsToRequiredLevels(string method, string path, string query, AccessLevel expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);

        TeamHubAccessRoutes.ResolveRequiredAccess(context.Request).Should().Be(expected);
    }

    [Fact]
    public void FlowOwner_WithEditAccess_CanDeleteOnlyOwnDiagrams()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "alice")],
                "Test"))
        };
        context.Items["TeamHub.AccessLevel"] = AccessLevel.Edit;
        var permissions = new TeamHubFlowPermissionService(new HttpContextAccessor { HttpContext = context });

        permissions.CanDelete("alice").Should().BeTrue();
        permissions.CanDelete("bob").Should().BeFalse();
    }

    [Theory]
    [InlineData(AccessLevel.NoAccess, AccessLevel.ReadOnly, false)]
    [InlineData(AccessLevel.ReadOnly, AccessLevel.ReadOnly, true)]
    [InlineData(AccessLevel.Create, AccessLevel.Create, true)]
    [InlineData(AccessLevel.Create, AccessLevel.Edit, false)]
    [InlineData(AccessLevel.Edit, AccessLevel.Create, true)]
    [InlineData(AccessLevel.Edit, AccessLevel.Edit, true)]
    [InlineData(AccessLevel.Delete, AccessLevel.Delete, true)]
    [InlineData(AccessLevel.FullAccess, AccessLevel.FullAccess, true)]
    public void AccessLevels_EnforceTheConfiguredHierarchy(AccessLevel granted, AccessLevel required, bool expected)
    {
        granted.Allows(required).Should().Be(expected);
    }

    private sealed class AccessTestHost : IAsyncDisposable
    {
        private readonly string directory;
        public ServiceProvider Services { get; }
        public string DatabasePath { get; }

        private AccessTestHost(string directory, string databasePath, ServiceProvider services)
        {
            this.directory = directory;
            DatabasePath = databasePath;
            Services = services;
        }

        public static async Task<AccessTestHost> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"teamhub-access-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var databasePath = Path.Combine(directory, "access.db");
            var services = new ServiceCollection();
            services.AddWorkflowAuthentication(
                $"Data Source={databasePath};Pooling=False",
                options =>
                {
                    options.UserId = "admin@example.com";
                    options.DisplayName = "Test Admin";
                    options.Password = "bootstrap123";
                });
            var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IUserAccessService>().InitializeAsync();
            return new AccessTestHost(directory, databasePath, provider);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
