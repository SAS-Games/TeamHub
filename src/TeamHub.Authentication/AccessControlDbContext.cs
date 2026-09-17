using Microsoft.EntityFrameworkCore;

namespace TeamHub.Authentication;

internal sealed class AuthorizedUserEntity
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string NormalizedUserId { get; set; } = string.Empty;
    public string? Gid { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string UserType { get; set; } = TeamHubUserTypes.Registered;
    public bool IsActive { get; set; } = true;
    public bool IsRegistered { get; set; }
    public string? PasswordHash { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

internal sealed class ModulePermissionEntity
{
    public int Id { get; set; }
    public string Module { get; set; } = string.Empty;
    public string UserType { get; set; } = string.Empty;
    public AccessLevel AccessLevel { get; set; }
}

internal sealed class UserModulePermissionEntity
{
    public int Id { get; set; }
    public Guid AuthorizedUserId { get; set; }
    public string Module { get; set; } = string.Empty;
    public AccessLevel AccessLevel { get; set; }
}

internal sealed class AccessSettingEntity
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

internal sealed class AccessControlDbContext(DbContextOptions<AccessControlDbContext> options) : DbContext(options)
{
    public DbSet<AuthorizedUserEntity> AuthorizedUsers => Set<AuthorizedUserEntity>();
    public DbSet<ModulePermissionEntity> ModulePermissions => Set<ModulePermissionEntity>();
    public DbSet<UserModulePermissionEntity> UserModulePermissions => Set<UserModulePermissionEntity>();
    public DbSet<AccessSettingEntity> Settings => Set<AccessSettingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<AuthorizedUserEntity>();
        user.ToTable("AuthorizedUsers");
        user.HasKey(item => item.Id);
        user.Property(item => item.UserId).HasMaxLength(256).IsRequired();
        user.Property(item => item.NormalizedUserId).HasMaxLength(256).IsRequired();
        user.Property(item => item.Gid).HasMaxLength(32);
        user.Property(item => item.DisplayName).HasMaxLength(256).IsRequired();
        user.Property(item => item.UserType).HasMaxLength(32).IsRequired();
        user.Property(item => item.PasswordHash).HasMaxLength(1000);
        user.HasIndex(item => item.NormalizedUserId).IsUnique();
        user.HasIndex(item => item.Gid).IsUnique();

        var permission = modelBuilder.Entity<ModulePermissionEntity>();
        permission.ToTable("ModulePermissions");
        permission.HasKey(item => item.Id);
        permission.Property(item => item.Module).HasMaxLength(100).IsRequired();
        permission.Property(item => item.UserType).HasMaxLength(32).IsRequired();
        permission.HasIndex(item => new { item.Module, item.UserType }).IsUnique();

        var userPermission = modelBuilder.Entity<UserModulePermissionEntity>();
        userPermission.ToTable("UserModulePermissions");
        userPermission.HasKey(item => item.Id);
        userPermission.Property(item => item.Module).HasMaxLength(100).IsRequired();
        userPermission.HasIndex(item => new { item.AuthorizedUserId, item.Module }).IsUnique();
        userPermission.HasOne<AuthorizedUserEntity>()
            .WithMany()
            .HasForeignKey(item => item.AuthorizedUserId)
            .OnDelete(DeleteBehavior.Cascade);

        var setting = modelBuilder.Entity<AccessSettingEntity>();
        setting.ToTable("AccessSettings");
        setting.HasKey(item => item.Key);
        setting.Property(item => item.Key).HasMaxLength(100);
        setting.Property(item => item.Value).HasMaxLength(500);
    }
}
