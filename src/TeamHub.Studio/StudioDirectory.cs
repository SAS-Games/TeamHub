using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TeamHub.Studio;

public sealed class StudioDetails
{
    public string Id { get; set; } = string.Empty;
    public string StudioName { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string StudioGroup { get; set; } = "Ungrouped";
    public int? GroupDisplayOrder { get; set; }
    public int? StudioDisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public string Location { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = TimeZoneInfo.Utc.Id;
    public List<StudioContact> OurContacts { get; set; } = [];
    public List<StudioTeamMember> TeamMembers { get; set; } = [];
    public List<StudioDevelopmentTool> DevelopmentTools { get; set; } = [];
    public List<StudioImportantLink> ImportantLinks { get; set; } = [];
}

public sealed class StudioTeamMember
{
    public string Name { get; set; } = string.Empty;
    public string RolesAndResponsibilities { get; set; } = string.Empty;
    public string? EmailId { get; set; }
}

public sealed class StudioDevelopmentTool
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class StudioImportantLink
{
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public interface IStudioDirectoryService
{
    Task<IReadOnlyList<StudioDetails>> GetStudiosAsync(CancellationToken cancellationToken = default);
    Task<StudioDetails?> GetStudioAsync(string id, CancellationToken cancellationToken = default);
    Task<StudioDetails> SaveStudioAsync(StudioDetails studio, CancellationToken cancellationToken = default);
    Task DeleteStudioAsync(string id, CancellationToken cancellationToken = default);
}

public interface IStudioDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

internal sealed class StudioRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string StudioName { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string StudioGroup { get; set; } = "Ungrouped";
    public int? StudioDisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public string Location { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = TimeZoneInfo.Utc.Id;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<StudioTeamMemberRecord> TeamMembers { get; set; } = new List<StudioTeamMemberRecord>();
    public ICollection<StudioContactRecord> OurContacts { get; set; } = new List<StudioContactRecord>();
    public ICollection<StudioDevelopmentToolsRecord> DevelopmentTools { get; set; } = new List<StudioDevelopmentToolsRecord>();
    public ICollection<StudioImportantLinkRecord> ImportantLinks { get; set; } = new List<StudioImportantLinkRecord>();
}

internal sealed class StudioGroupOrderRecord
{
    public string NormalizedGroupName { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class StudioTeamMemberRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudioRecordId { get; set; }
    public int DisplayOrder { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RolesAndResponsibilities { get; set; } = string.Empty;
    public string? EmailId { get; set; }
    public StudioRecord Studio { get; set; } = null!;
}

internal sealed class StudioDevelopmentToolsRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudioRecordId { get; set; }
    public int DisplayOrder { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public StudioRecord Studio { get; set; } = null!;
}

internal sealed class StudioImportantLinkRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudioRecordId { get; set; }
    public int DisplayOrder { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public StudioRecord Studio { get; set; } = null!;
}

internal sealed class AtlassianIntegrationSettingsRecord
{
    public int Id { get; set; } = 1;
    public bool JiraEnabled { get; set; }
    public string JiraBaseUrl { get; set; } = string.Empty;
    public string JiraSearchApiPath { get; set; } = "/rest/api/2/search";
    public int JiraMaxResults { get; set; } = 100;
    public string JiraDefaultSupportComponent { get; set; } = "StudioSupport";
    public bool ConfluenceEnabled { get; set; }
    public string ConfluenceBaseUrl { get; set; } = string.Empty;
    public string ConfluenceContentApiPath { get; set; } = "/rest/api/content";
    public string ConfluenceSpaceKey { get; set; } = string.Empty;
    public string ConfluenceParentPageId { get; set; } = string.Empty;
    public string ConfluenceYearTitlePattern { get; set; } = "{Year}";
    public string ConfluenceMonthTitlePattern { get; set; } = "{Month}/{Year}";
    public string ConfluenceWeeklyTitlePattern { get; set; } = "{WeekStart:dd/MM}-{WeekEnd:dd/MM}";
    public string? DefaultJiraTokenProtected { get; set; }
    public string? DefaultConfluenceTokenProtected { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class AtlassianPrivilegedAccessRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public string NormalizedUserId { get; set; } = string.Empty;
    public bool JiraReadOnlyAccess { get; set; }
    public bool ConfluenceReadOnlyAccess { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class StudioAtlassianMappingRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudioRecordId { get; set; }
    public string JiraProjectKeys { get; set; } = string.Empty;
    public string JiraStudioComponent { get; set; } = string.Empty;
    public string JiraSupportComponent { get; set; } = string.Empty;
    public string ConfluenceStudioIdentifier { get; set; } = string.Empty;
    public string ConfluenceSpaceKey { get; set; } = string.Empty;
    public string ConfluenceParentPageId { get; set; } = string.Empty;
    public string ConfluenceWeeklyTitlePattern { get; set; } = "{StudioName} Weekly Update - {WeekStart:yyyy-MM-dd}";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public StudioRecord Studio { get; set; } = null!;
}

internal sealed class AtlassianUserCredentialRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public string NormalizedUserId { get; set; } = string.Empty;
    public string? JiraTokenProtected { get; set; }
    public string? ConfluenceTokenProtected { get; set; }
    public DateTime? JiraConnectedAtUtc { get; set; }
    public DateTime? ConfluenceConnectedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class StudioDbContext(DbContextOptions<StudioDbContext> options) : DbContext(options)
{
    public DbSet<StudioRecord> Studios => Set<StudioRecord>();
    public DbSet<StudioGroupOrderRecord> StudioGroupOrders => Set<StudioGroupOrderRecord>();
    public DbSet<StudioContactRecord> StudioContacts => Set<StudioContactRecord>();
    public DbSet<StudioTeamMemberRecord> StudioTeamMembers => Set<StudioTeamMemberRecord>();
    public DbSet<StudioDevelopmentToolsRecord> StudioDevelopmentTools => Set<StudioDevelopmentToolsRecord>();
    public DbSet<StudioImportantLinkRecord> StudioImportantLinks => Set<StudioImportantLinkRecord>();
    public DbSet<AtlassianIntegrationSettingsRecord> AtlassianIntegrationSettings => Set<AtlassianIntegrationSettingsRecord>();
    public DbSet<StudioAtlassianMappingRecord> StudioAtlassianMappings => Set<StudioAtlassianMappingRecord>();
    public DbSet<AtlassianUserCredentialRecord> AtlassianUserCredentials => Set<AtlassianUserCredentialRecord>();
    public DbSet<AtlassianPrivilegedAccessRecord> AtlassianPrivilegedAccess => Set<AtlassianPrivilegedAccessRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StudioRecord>(entity =>
        {
            entity.ToTable("Studios");
            entity.Property(x => x.StudioName).HasMaxLength(256);
            entity.Property(x => x.ProjectName).HasMaxLength(256);
            entity.Property(x => x.StudioGroup).HasMaxLength(128);
            entity.Property(x => x.Location).HasMaxLength(256);
            entity.Property(x => x.TimeZoneId).HasMaxLength(256);
            entity.HasIndex(x => new { x.StudioName, x.ProjectName }).IsUnique();
        });

        modelBuilder.Entity<StudioGroupOrderRecord>(entity =>
        {
            entity.ToTable("StudioGroupOrders");
            entity.HasKey(x => x.NormalizedGroupName);
            entity.Property(x => x.NormalizedGroupName).HasMaxLength(128);
            entity.Property(x => x.GroupName).HasMaxLength(128);
        });

        modelBuilder.Entity<StudioContactRecord>(entity =>
        {
            entity.ToTable("StudioContacts");
            entity.Property(x => x.Name).HasMaxLength(256);
            entity.Property(x => x.Role).HasMaxLength(256);
            entity.HasIndex(x => new { x.StudioRecordId, x.DisplayOrder });
            entity.HasOne(x => x.Studio)
                .WithMany(x => x.OurContacts)
                .HasForeignKey(x => x.StudioRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StudioTeamMemberRecord>(entity =>
        {
            entity.ToTable("StudioTeamMembers");
            entity.Property(x => x.Name).HasMaxLength(256);
            entity.Property(x => x.EmailId).HasMaxLength(256);
            entity.HasIndex(x => new { x.StudioRecordId, x.DisplayOrder });
            entity.HasOne(x => x.Studio)
                .WithMany(x => x.TeamMembers)
                .HasForeignKey(x => x.StudioRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StudioDevelopmentToolsRecord>(entity =>
        {
            entity.ToTable("StudioDevelopmentTools");
            entity.Property(x => x.Name).HasMaxLength(256);
            entity.HasIndex(x => new { x.StudioRecordId, x.DisplayOrder });
            entity.HasOne(x => x.Studio)
                .WithMany(x => x.DevelopmentTools)
                .HasForeignKey(x => x.StudioRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StudioImportantLinkRecord>(entity =>
        {
            entity.ToTable("StudioImportantLinks");
            entity.Property(x => x.Label).HasMaxLength(256);
            entity.Property(x => x.Url).HasMaxLength(1024);
            entity.HasIndex(x => new { x.StudioRecordId, x.DisplayOrder });
            entity.HasOne(x => x.Studio)
                .WithMany(x => x.ImportantLinks)
                .HasForeignKey(x => x.StudioRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AtlassianIntegrationSettingsRecord>(entity =>
        {
            entity.ToTable("AtlassianIntegrationSettings");
            entity.Property(x => x.JiraBaseUrl).HasMaxLength(2048);
            entity.Property(x => x.JiraSearchApiPath).HasMaxLength(512);
            entity.Property(x => x.JiraDefaultSupportComponent).HasMaxLength(256);
            entity.Property(x => x.ConfluenceBaseUrl).HasMaxLength(2048);
            entity.Property(x => x.ConfluenceContentApiPath).HasMaxLength(512);
            entity.Property(x => x.ConfluenceSpaceKey).HasMaxLength(256);
            entity.Property(x => x.ConfluenceParentPageId).HasMaxLength(256);
            entity.Property(x => x.ConfluenceYearTitlePattern).HasMaxLength(256);
            entity.Property(x => x.ConfluenceMonthTitlePattern).HasMaxLength(256);
            entity.Property(x => x.ConfluenceWeeklyTitlePattern).HasMaxLength(512);
        });

        modelBuilder.Entity<AtlassianPrivilegedAccessRecord>(entity =>
        {
            entity.ToTable("AtlassianPrivilegedAccess");
            entity.Property(x => x.UserId).HasMaxLength(320);
            entity.Property(x => x.NormalizedUserId).HasMaxLength(320);
            entity.HasIndex(x => x.NormalizedUserId).IsUnique();
        });

        modelBuilder.Entity<StudioAtlassianMappingRecord>(entity =>
        {
            entity.ToTable("AtlassianStudioMappings");
            entity.Property(x => x.JiraProjectKeys).HasMaxLength(2048);
            entity.Property(x => x.JiraStudioComponent).HasMaxLength(256);
            entity.Property(x => x.JiraSupportComponent).HasMaxLength(256);
            entity.Property(x => x.ConfluenceStudioIdentifier).HasMaxLength(256);
            entity.Property(x => x.ConfluenceSpaceKey).HasMaxLength(256);
            entity.Property(x => x.ConfluenceParentPageId).HasMaxLength(256);
            entity.Property(x => x.ConfluenceWeeklyTitlePattern).HasMaxLength(512);
            entity.HasIndex(x => x.StudioRecordId).IsUnique();
            entity.HasOne(x => x.Studio)
                .WithMany()
                .HasForeignKey(x => x.StudioRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AtlassianUserCredentialRecord>(entity =>
        {
            entity.ToTable("AtlassianUserCredentials");
            entity.Property(x => x.UserId).HasMaxLength(320);
            entity.Property(x => x.NormalizedUserId).HasMaxLength(320);
            entity.HasIndex(x => x.NormalizedUserId).IsUnique();
        });
    }
}

internal sealed class SqliteStudioDatabaseInitializer(
    StudioDbContext dbContext,
    IConfiguration configuration) : IStudioDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS Studios (
                    Id TEXT NOT NULL CONSTRAINT PK_Studios PRIMARY KEY,
                    StudioName TEXT NOT NULL,
                    ProjectName TEXT NOT NULL,
                    StudioGroup TEXT NOT NULL DEFAULT 'Ungrouped',
                    StudioDisplayOrder INTEGER NULL,
                    IsActive INTEGER NOT NULL DEFAULT 1,
                    Location TEXT NOT NULL,
                    TimeZoneId TEXT NOT NULL DEFAULT 'UTC',
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                """, cancellationToken);

            var connection = (SqliteConnection)dbContext.Database.GetDbConnection();
            if (!await MainColumnExistsAsync(connection, "Studios", "TimeZoneId", cancellationToken))
            {
                await dbContext.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE Studios
                    ADD COLUMN TimeZoneId TEXT NOT NULL DEFAULT 'UTC';
                    """, cancellationToken);
            }
            if (!await MainColumnExistsAsync(connection, "Studios", "StudioGroup", cancellationToken))
            {
                await dbContext.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE Studios
                    ADD COLUMN StudioGroup TEXT NOT NULL DEFAULT 'Ungrouped';
                    """, cancellationToken);
            }
            if (!await MainColumnExistsAsync(connection, "Studios", "IsActive", cancellationToken))
            {
                await dbContext.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE Studios
                    ADD COLUMN IsActive INTEGER NOT NULL DEFAULT 1;
                    """, cancellationToken);
            }
            if (!await MainColumnExistsAsync(connection, "Studios", "StudioDisplayOrder", cancellationToken))
            {
                await dbContext.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE Studios
                    ADD COLUMN StudioDisplayOrder INTEGER NULL;
                    """, cancellationToken);
            }

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS StudioGroupOrders (
                    NormalizedGroupName TEXT NOT NULL CONSTRAINT PK_StudioGroupOrders PRIMARY KEY,
                    GroupName TEXT NOT NULL,
                    DisplayOrder INTEGER NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE UNIQUE INDEX IF NOT EXISTS IX_Studios_StudioName_ProjectName
                ON Studios (StudioName, ProjectName);
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS StudioContacts (
                    Id TEXT NOT NULL CONSTRAINT PK_StudioContacts PRIMARY KEY,
                    StudioRecordId TEXT NOT NULL,
                    DisplayOrder INTEGER NOT NULL,
                    Name TEXT NOT NULL,
                    Role TEXT NOT NULL,
                    CONSTRAINT FK_StudioContacts_Studios_StudioRecordId
                        FOREIGN KEY (StudioRecordId) REFERENCES Studios (Id) ON DELETE CASCADE
                );
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS IX_StudioContacts_StudioRecordId_DisplayOrder
                ON StudioContacts (StudioRecordId, DisplayOrder);
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS StudioTeamMembers (
                    Id TEXT NOT NULL CONSTRAINT PK_StudioTeamMembers PRIMARY KEY,
                    StudioRecordId TEXT NOT NULL,
                    DisplayOrder INTEGER NOT NULL,
                    Name TEXT NOT NULL,
                    RolesAndResponsibilities TEXT NOT NULL,
                    EmailId TEXT NULL,
                    CONSTRAINT FK_StudioTeamMembers_Studios_StudioRecordId
                        FOREIGN KEY (StudioRecordId) REFERENCES Studios (Id) ON DELETE CASCADE
                );
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS IX_StudioTeamMembers_StudioRecordId_DisplayOrder
                ON StudioTeamMembers (StudioRecordId, DisplayOrder);
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS StudioDevelopmentTools (
                    Id TEXT NOT NULL CONSTRAINT PK_StudioDevelopmentTools PRIMARY KEY,
                    StudioRecordId TEXT NOT NULL,
                    DisplayOrder INTEGER NOT NULL,
                    Name TEXT NOT NULL,
                    Description TEXT NOT NULL,
                    CONSTRAINT FK_StudioDevelopmentTools_Studios_StudioRecordId
                        FOREIGN KEY (StudioRecordId) REFERENCES Studios (Id) ON DELETE CASCADE
                );
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS IX_StudioDevelopmentTools_StudioRecordId_DisplayOrder
                ON StudioDevelopmentTools (StudioRecordId, DisplayOrder);
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS StudioImportantLinks (
                    Id TEXT NOT NULL CONSTRAINT PK_StudioImportantLinks PRIMARY KEY,
                    StudioRecordId TEXT NOT NULL,
                    DisplayOrder INTEGER NOT NULL,
                    Label TEXT NOT NULL,
                    Url TEXT NOT NULL,
                    Description TEXT NOT NULL,
                    CONSTRAINT FK_StudioImportantLinks_Studios_StudioRecordId
                        FOREIGN KEY (StudioRecordId) REFERENCES Studios (Id) ON DELETE CASCADE
                );
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS IX_StudioImportantLinks_StudioRecordId_DisplayOrder
                ON StudioImportantLinks (StudioRecordId, DisplayOrder);
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS AtlassianIntegrationSettings (
                    Id INTEGER NOT NULL CONSTRAINT PK_AtlassianIntegrationSettings PRIMARY KEY,
                    JiraEnabled INTEGER NOT NULL,
                    JiraBaseUrl TEXT NOT NULL,
                    JiraSearchApiPath TEXT NOT NULL,
                    JiraMaxResults INTEGER NOT NULL,
                    JiraDefaultSupportComponent TEXT NOT NULL,
                    ConfluenceEnabled INTEGER NOT NULL,
                    ConfluenceBaseUrl TEXT NOT NULL,
                    ConfluenceContentApiPath TEXT NOT NULL,
                    ConfluenceSpaceKey TEXT NOT NULL DEFAULT '',
                    ConfluenceParentPageId TEXT NOT NULL DEFAULT '',
                    ConfluenceYearTitlePattern TEXT NOT NULL DEFAULT '{{Year}}',
                    ConfluenceMonthTitlePattern TEXT NOT NULL DEFAULT '{{Month}}/{{Year}}',
                    ConfluenceWeeklyTitlePattern TEXT NOT NULL DEFAULT '{{WeekStart:dd/MM}}-{{WeekEnd:dd/MM}}',
                    DefaultJiraTokenProtected TEXT NULL,
                    DefaultConfluenceTokenProtected TEXT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                """, cancellationToken);

            if (!await MainColumnExistsAsync(connection, "AtlassianIntegrationSettings", "DefaultJiraTokenProtected", cancellationToken))
            {
                await dbContext.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE AtlassianIntegrationSettings
                    ADD COLUMN DefaultJiraTokenProtected TEXT NULL;
                    """, cancellationToken);
            }
            if (!await MainColumnExistsAsync(connection, "AtlassianIntegrationSettings", "ConfluenceSpaceKey", cancellationToken))
            {
                await dbContext.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE AtlassianIntegrationSettings
                    ADD COLUMN ConfluenceSpaceKey TEXT NOT NULL DEFAULT '';
                    """, cancellationToken);
            }
            if (!await MainColumnExistsAsync(connection, "AtlassianIntegrationSettings", "ConfluenceParentPageId", cancellationToken))
            {
                await dbContext.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE AtlassianIntegrationSettings
                    ADD COLUMN ConfluenceParentPageId TEXT NOT NULL DEFAULT '';
                    """, cancellationToken);
            }
            if (!await MainColumnExistsAsync(connection, "AtlassianIntegrationSettings", "ConfluenceWeeklyTitlePattern", cancellationToken))
            {
                await dbContext.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE AtlassianIntegrationSettings
                    ADD COLUMN ConfluenceWeeklyTitlePattern TEXT NOT NULL DEFAULT '{{WeekStart:dd/MM}}-{{WeekEnd:dd/MM}}';
                    """, cancellationToken);
            }
            if (!await MainColumnExistsAsync(connection, "AtlassianIntegrationSettings", "ConfluenceYearTitlePattern", cancellationToken))
            {
                await dbContext.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE AtlassianIntegrationSettings
                    ADD COLUMN ConfluenceYearTitlePattern TEXT NOT NULL DEFAULT '{{Year}}';
                    """, cancellationToken);
            }
            if (!await MainColumnExistsAsync(connection, "AtlassianIntegrationSettings", "ConfluenceMonthTitlePattern", cancellationToken))
            {
                await dbContext.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE AtlassianIntegrationSettings
                    ADD COLUMN ConfluenceMonthTitlePattern TEXT NOT NULL DEFAULT '{{Month}}/{{Year}}';
                    """, cancellationToken);
            }
            if (!await MainColumnExistsAsync(connection, "AtlassianIntegrationSettings", "DefaultConfluenceTokenProtected", cancellationToken))
            {
                await dbContext.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE AtlassianIntegrationSettings
                    ADD COLUMN DefaultConfluenceTokenProtected TEXT NULL;
                    """, cancellationToken);
            }

            await dbContext.Database.ExecuteSqlRawAsync("""
                INSERT OR IGNORE INTO AtlassianIntegrationSettings
                    (Id, JiraEnabled, JiraBaseUrl, JiraSearchApiPath, JiraMaxResults,
                     JiraDefaultSupportComponent, ConfluenceEnabled, ConfluenceBaseUrl,
                     ConfluenceContentApiPath, ConfluenceSpaceKey, ConfluenceParentPageId,
                     ConfluenceYearTitlePattern, ConfluenceMonthTitlePattern,
                     ConfluenceWeeklyTitlePattern, DefaultJiraTokenProtected,
                     DefaultConfluenceTokenProtected, UpdatedAtUtc)
                VALUES
                    (1, 0, '', '/rest/api/2/search', 100,
                     'StudioSupport', 0, '', '/rest/api/content', '', '',
                     '{{Year}}', '{{Month}}/{{Year}}',
                     '{{WeekStart:dd/MM}}-{{WeekEnd:dd/MM}}', NULL, NULL, CURRENT_TIMESTAMP);
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                UPDATE AtlassianIntegrationSettings
                SET JiraDefaultSupportComponent = 'StudioSupport'
                WHERE JiraDefaultSupportComponent = 'studio_Support';
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS AtlassianPrivilegedAccess (
                    Id TEXT NOT NULL CONSTRAINT PK_AtlassianPrivilegedAccess PRIMARY KEY,
                    UserId TEXT NOT NULL,
                    NormalizedUserId TEXT NOT NULL,
                    JiraReadOnlyAccess INTEGER NOT NULL,
                    ConfluenceReadOnlyAccess INTEGER NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE UNIQUE INDEX IF NOT EXISTS IX_AtlassianPrivilegedAccess_NormalizedUserId
                ON AtlassianPrivilegedAccess (NormalizedUserId);
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS AtlassianStudioMappings (
                    Id TEXT NOT NULL CONSTRAINT PK_AtlassianStudioMappings PRIMARY KEY,
                    StudioRecordId TEXT NOT NULL,
                    JiraProjectKeys TEXT NOT NULL,
                    JiraStudioComponent TEXT NOT NULL,
                    JiraSupportComponent TEXT NOT NULL,
                    ConfluenceStudioIdentifier TEXT NOT NULL DEFAULT '',
                    ConfluenceSpaceKey TEXT NOT NULL,
                    ConfluenceParentPageId TEXT NOT NULL,
                    ConfluenceWeeklyTitlePattern TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    CONSTRAINT FK_AtlassianStudioMappings_Studios_StudioRecordId
                        FOREIGN KEY (StudioRecordId) REFERENCES Studios (Id) ON DELETE CASCADE
                );
                """, cancellationToken);

            if (!await MainColumnExistsAsync(connection, "AtlassianStudioMappings", "ConfluenceStudioIdentifier", cancellationToken))
            {
                await dbContext.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE AtlassianStudioMappings
                    ADD COLUMN ConfluenceStudioIdentifier TEXT NOT NULL DEFAULT '';
                    """, cancellationToken);
            }

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE UNIQUE INDEX IF NOT EXISTS IX_AtlassianStudioMappings_StudioRecordId
                ON AtlassianStudioMappings (StudioRecordId);
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                UPDATE AtlassianIntegrationSettings
                SET ConfluenceSpaceKey = COALESCE(
                        NULLIF(ConfluenceSpaceKey, ''),
                        (SELECT NULLIF(ConfluenceSpaceKey, '') FROM AtlassianStudioMappings
                         WHERE ConfluenceSpaceKey <> '' LIMIT 1),
                        ''),
                    ConfluenceParentPageId = COALESCE(
                        NULLIF(ConfluenceParentPageId, ''),
                        (SELECT NULLIF(ConfluenceParentPageId, '') FROM AtlassianStudioMappings
                         WHERE ConfluenceParentPageId <> '' LIMIT 1),
                        ''),
                    ConfluenceWeeklyTitlePattern = COALESCE(
                        NULLIF(ConfluenceWeeklyTitlePattern, ''),
                        (SELECT NULLIF(ConfluenceWeeklyTitlePattern, '') FROM AtlassianStudioMappings
                         WHERE ConfluenceWeeklyTitlePattern <> '' LIMIT 1),
                        '{{WeekStart:dd/MM}}-{{WeekEnd:dd/MM}}')
                WHERE Id = 1;
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS AtlassianUserCredentials (
                    Id TEXT NOT NULL CONSTRAINT PK_AtlassianUserCredentials PRIMARY KEY,
                    UserId TEXT NOT NULL,
                    NormalizedUserId TEXT NOT NULL,
                    JiraTokenProtected TEXT NULL,
                    ConfluenceTokenProtected TEXT NULL,
                    JiraConnectedAtUtc TEXT NULL,
                    ConfluenceConnectedAtUtc TEXT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE UNIQUE INDEX IF NOT EXISTS IX_AtlassianUserCredentials_NormalizedUserId
                ON AtlassianUserCredentials (NormalizedUserId);
                """, cancellationToken);

            await MoveLegacyStudioTablesAsync(cancellationToken);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private async Task MoveLegacyStudioTablesAsync(CancellationToken cancellationToken)
    {
        var legacyConnectionString = configuration.GetConnectionString("WorkflowDb");
        var studioConnectionString = dbContext.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(legacyConnectionString) || string.IsNullOrWhiteSpace(studioConnectionString))
        {
            return;
        }

        var legacyPath = GetDatabasePath(legacyConnectionString);
        var studioPath = GetDatabasePath(studioConnectionString);
        if (legacyPath is null || studioPath is null
            || string.Equals(legacyPath, studioPath, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(legacyPath))
        {
            return;
        }

        var connection = (SqliteConnection)dbContext.Database.GetDbConnection();
        await using (var attach = connection.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $legacyPath AS legacy;";
            attach.Parameters.AddWithValue("$legacyPath", legacyPath);
            await attach.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            if (!await TableExistsAsync(connection, "Studios", cancellationToken))
            {
                return;
            }

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await ExecuteAsync(connection, transaction, """
                INSERT OR IGNORE INTO main.Studios
                    (Id, StudioName, ProjectName, Location, CreatedAtUtc, UpdatedAtUtc)
                SELECT Id, StudioName, ProjectName, Location, CreatedAtUtc, UpdatedAtUtc
                FROM legacy.Studios;
                """, cancellationToken);

            var childTables = new[]
            {
                new LegacyTable("StudioContacts", "Id, StudioRecordId, DisplayOrder, Name, Role"),
                new LegacyTable("StudioTeamMembers", "Id, StudioRecordId, DisplayOrder, Name, RolesAndResponsibilities, EmailId"),
                new LegacyTable("StudioDevelopmentTools", "Id, StudioRecordId, DisplayOrder, Name, Description"),
                new LegacyTable("StudioImportantLinks", "Id, StudioRecordId, DisplayOrder, Label, Url, Description")
            };

            var existingChildTables = new List<LegacyTable>();
            foreach (var table in childTables)
            {
                if (!await TableExistsAsync(connection, table.Name, cancellationToken, transaction))
                {
                    continue;
                }

                existingChildTables.Add(table);
                await ExecuteAsync(connection, transaction, $"""
                    INSERT OR IGNORE INTO main.{table.Name} ({table.Columns})
                    SELECT {table.Columns}
                    FROM legacy.{table.Name} AS source
                    WHERE EXISTS (
                        SELECT 1 FROM main.Studios AS studio WHERE studio.Id = source.StudioRecordId
                    );
                    """, cancellationToken);
            }

            var hasGroupOrders = await TableExistsAsync(
                connection,
                "StudioGroupOrders",
                cancellationToken,
                transaction);
            if (hasGroupOrders)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT OR IGNORE INTO main.StudioGroupOrders
                        (NormalizedGroupName, GroupName, DisplayOrder, UpdatedAtUtc)
                    SELECT NormalizedGroupName, GroupName, DisplayOrder, UpdatedAtUtc
                    FROM legacy.StudioGroupOrders;
                    """, cancellationToken);
            }

            var migrationComplete = await AllRowsMovedAsync(connection, transaction, "Studios", cancellationToken);
            foreach (var table in existingChildTables)
            {
                migrationComplete = migrationComplete
                    && await AllRowsMovedAsync(connection, transaction, table.Name, cancellationToken);
            }
            if (hasGroupOrders)
            {
                migrationComplete = migrationComplete
                    && await AllRowsMovedAsync(
                        connection,
                        transaction,
                        "StudioGroupOrders",
                        cancellationToken,
                        "NormalizedGroupName");
            }

            if (migrationComplete)
            {
                foreach (var table in existingChildTables)
                {
                    await ExecuteAsync(connection, transaction, $"DROP TABLE legacy.{table.Name};", cancellationToken);
                }
                if (hasGroupOrders)
                {
                    await ExecuteAsync(connection, transaction, "DROP TABLE legacy.StudioGroupOrders;", cancellationToken);
                }
                await ExecuteAsync(connection, transaction, "DROP TABLE legacy.Studios;", cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            await using var detach = connection.CreateCommand();
            detach.CommandText = "DETACH DATABASE legacy;";
            await detach.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string? GetDatabasePath(string connectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource == ":memory:")
        {
            return null;
        }

        return Path.GetFullPath(dataSource);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM legacy.sqlite_master WHERE type = 'table' AND name = $tableName LIMIT 1;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> MainColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA main.table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> AllRowsMovedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken,
        string keyColumn = "Id")
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT COUNT(*)
            FROM legacy.{tableName} AS source
            WHERE NOT EXISTS (
                SELECT 1 FROM main.{tableName} AS target
                WHERE target.{keyColumn} = source.{keyColumn}
            );
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 0;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record LegacyTable(string Name, string Columns);
}

internal sealed class StudioContactRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudioRecordId { get; set; }
    public int DisplayOrder { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public StudioRecord Studio { get; set; } = null!;
}

public sealed class StudioContact
{
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

internal sealed class SqliteStudioDirectoryService(StudioDbContext dbContext) : IStudioDirectoryService
{
    public async Task<IReadOnlyList<StudioDetails>> GetStudiosAsync(CancellationToken cancellationToken = default)
    {
        var studios = await dbContext.Studios
            .AsNoTracking()
            .Include(studio => studio.OurContacts)
            .Include(studio => studio.TeamMembers)
            .Include(studio => studio.DevelopmentTools)
            .Include(studio => studio.ImportantLinks)
            .ToListAsync(cancellationToken);
        var groupOrders = await dbContext.StudioGroupOrders
            .AsNoTracking()
            .ToDictionaryAsync(item => item.NormalizedGroupName, item => item.DisplayOrder, cancellationToken);

        return studios
            .Select(studio =>
            {
                var hasGroupOrder = groupOrders.TryGetValue(
                    NormalizeStudioGroupKey(studio.StudioGroup),
                    out var groupOrder);
                return ToDetails(studio, hasGroupOrder ? groupOrder : null);
            })
            .OrderBy(studio => studio.GroupDisplayOrder ?? int.MaxValue)
            .ThenBy(studio => studio.StudioGroup, StringComparer.OrdinalIgnoreCase)
            .ThenBy(studio => studio.StudioDisplayOrder ?? int.MaxValue)
            .ThenBy(studio => studio.StudioName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(studio => studio.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<StudioDetails?> GetStudioAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var studioId))
        {
            return null;
        }

        var studio = await dbContext.Studios
            .AsNoTracking()
            .Include(item => item.OurContacts)
            .Include(item => item.TeamMembers)
            .Include(item => item.DevelopmentTools)
            .Include(item => item.ImportantLinks)
            .FirstOrDefaultAsync(item => item.Id == studioId, cancellationToken);

        if (studio is null)
        {
            return null;
        }

        var groupOrder = await dbContext.StudioGroupOrders
            .AsNoTracking()
            .Where(item => item.NormalizedGroupName == NormalizeStudioGroupKey(studio.StudioGroup))
            .Select(item => (int?)item.DisplayOrder)
            .FirstOrDefaultAsync(cancellationToken);
        return ToDetails(studio, groupOrder);
    }

    public async Task<StudioDetails> SaveStudioAsync(StudioDetails studio, CancellationToken cancellationToken = default)
    {
        StudioRecord record;
        var existingStudio = false;
        var originalGroup = string.Empty;
        if (Guid.TryParse(studio.Id, out var studioId))
        {
            record = await dbContext.Studios
                .FirstOrDefaultAsync(item => item.Id == studioId, cancellationToken)
                ?? new StudioRecord { Id = studioId, CreatedAtUtc = DateTime.UtcNow };
            existingStudio = dbContext.Entry(record).State != EntityState.Detached;
            originalGroup = record.StudioGroup;
        }
        else
        {
            record = new StudioRecord();
            dbContext.Studios.Add(record);
        }

        record.StudioName = studio.StudioName.Trim();
        record.ProjectName = studio.ProjectName.Trim();
        record.StudioGroup = NormalizeStudioGroup(studio.StudioGroup);
        record.StudioDisplayOrder = NormalizeDisplayOrder(studio.StudioDisplayOrder);
        record.IsActive = studio.IsActive;
        record.Location = studio.Location.Trim();
        record.TimeZoneId = NormalizeTimeZoneId(studio.TimeZoneId);
        record.UpdatedAtUtc = DateTime.UtcNow;

        if (dbContext.Entry(record).State == EntityState.Detached)
        {
            dbContext.Studios.Add(record);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await SaveGroupOrderAsync(
            record.StudioGroup,
            NormalizeDisplayOrder(studio.GroupDisplayOrder),
            existingStudio && string.Equals(
                NormalizeStudioGroupKey(originalGroup),
                NormalizeStudioGroupKey(record.StudioGroup),
                StringComparison.Ordinal),
            cancellationToken);
        await RemoveUnusedGroupOrderAsync(originalGroup, record.StudioGroup, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        await dbContext.StudioContacts
            .Where(contact => contact.StudioRecordId == record.Id)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.StudioTeamMembers
            .Where(member => member.StudioRecordId == record.Id)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.StudioDevelopmentTools
            .Where(tool => tool.StudioRecordId == record.Id)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.StudioImportantLinks
            .Where(link => link.StudioRecordId == record.Id)
            .ExecuteDeleteAsync(cancellationToken);

        dbContext.StudioContacts.AddRange(CreateContactRecords(record.Id, studio.OurContacts));
        dbContext.StudioTeamMembers.AddRange(CreateTeamMemberRecords(record.Id, studio.TeamMembers));
        dbContext.StudioDevelopmentTools.AddRange(CreateDevelopmentToolsRecords(record.Id, studio.DevelopmentTools));
        dbContext.StudioImportantLinks.AddRange(CreateImportantLinkRecords(record.Id, studio.ImportantLinks));
        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetStudioAsync(record.Id.ToString(), cancellationToken) ?? ToDetails(record, studio.GroupDisplayOrder);
    }

    public async Task DeleteStudioAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var studioId))
        {
            return;
        }

        var studio = await dbContext.Studios.FindAsync([studioId], cancellationToken);
        if (studio is null)
        {
            return;
        }

        var groupName = studio.StudioGroup;
        dbContext.Studios.Remove(studio);
        await dbContext.SaveChangesAsync(cancellationToken);
        await RemoveGroupOrderIfUnusedAsync(groupName, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static StudioDetails ToDetails(StudioRecord studio, int? groupDisplayOrder)
    {
        return new StudioDetails
        {
            Id = studio.Id.ToString(),
            StudioName = studio.StudioName,
            ProjectName = studio.ProjectName,
            StudioGroup = NormalizeStudioGroup(studio.StudioGroup),
            GroupDisplayOrder = groupDisplayOrder,
            StudioDisplayOrder = studio.StudioDisplayOrder,
            IsActive = studio.IsActive,
            Location = studio.Location,
            TimeZoneId = NormalizeTimeZoneId(studio.TimeZoneId),
            OurContacts = studio.OurContacts
                .OrderBy(contact => contact.DisplayOrder)
                .Select(contact => new StudioContact
                {
                    Name = contact.Name,
                    Role = contact.Role
                })
                .ToList(),
            TeamMembers = studio.TeamMembers
                .OrderBy(member => member.DisplayOrder)
                .Select(member => new StudioTeamMember
                {
                    Name = member.Name,
                    RolesAndResponsibilities = member.RolesAndResponsibilities,
                    EmailId = member.EmailId
                })
                .ToList(),
            DevelopmentTools = studio.DevelopmentTools
                .OrderBy(tool => tool.DisplayOrder)
                .Select(tool => new StudioDevelopmentTool
                {
                    Name = tool.Name,
                    Description = tool.Description
                })
                .ToList(),
            ImportantLinks = studio.ImportantLinks
                .OrderBy(link => link.DisplayOrder)
                .Select(link => new StudioImportantLink
                {
                    Label = link.Label,
                    Url = link.Url,
                    Description = link.Description
                })
                .ToList()
        };
    }

    private async Task SaveGroupOrderAsync(
        string groupName,
        int? displayOrder,
        bool clearWhenMissing,
        CancellationToken cancellationToken)
    {
        var normalizedGroupName = NormalizeStudioGroupKey(groupName);
        var existing = await dbContext.StudioGroupOrders
            .FirstOrDefaultAsync(item => item.NormalizedGroupName == normalizedGroupName, cancellationToken);

        if (!displayOrder.HasValue)
        {
            if (clearWhenMissing && existing is not null)
            {
                dbContext.StudioGroupOrders.Remove(existing);
            }
            return;
        }

        if (existing is null)
        {
            existing = new StudioGroupOrderRecord { NormalizedGroupName = normalizedGroupName };
            dbContext.StudioGroupOrders.Add(existing);
        }
        existing.GroupName = NormalizeStudioGroup(groupName);
        existing.DisplayOrder = displayOrder.Value;
        existing.UpdatedAtUtc = DateTime.UtcNow;
    }

    private async Task RemoveUnusedGroupOrderAsync(
        string originalGroup,
        string currentGroup,
        CancellationToken cancellationToken)
    {
        var originalKey = NormalizeStudioGroupKey(originalGroup);
        if (string.IsNullOrWhiteSpace(originalGroup)
            || string.Equals(originalKey, NormalizeStudioGroupKey(currentGroup), StringComparison.Ordinal)
            || await dbContext.Studios.AsNoTracking().AnyAsync(
                studio => studio.StudioGroup.ToUpper() == originalKey,
                cancellationToken))
        {
            return;
        }

        await RemoveGroupOrderIfUnusedAsync(originalGroup, cancellationToken);
    }

    private async Task RemoveGroupOrderIfUnusedAsync(
        string groupName,
        CancellationToken cancellationToken)
    {
        var groupKey = NormalizeStudioGroupKey(groupName);
        if (await dbContext.Studios.AsNoTracking().AnyAsync(
                studio => studio.StudioGroup.ToUpper() == groupKey,
                cancellationToken))
        {
            return;
        }

        var unused = await dbContext.StudioGroupOrders.FindAsync([groupKey], cancellationToken);
        if (unused is not null)
        {
            dbContext.StudioGroupOrders.Remove(unused);
        }
    }

    private static IEnumerable<StudioContactRecord> CreateContactRecords(
        Guid studioRecordId,
        IEnumerable<StudioContact> contacts)
    {
        var displayOrder = 0;
        foreach (var contact in contacts.Where(HasContactValue))
        {
            yield return new StudioContactRecord
            {
                StudioRecordId = studioRecordId,
                DisplayOrder = displayOrder,
                Name = contact.Name.Trim(),
                Role = contact.Role.Trim()
            };
            displayOrder++;
        }
    }

    private static IEnumerable<StudioTeamMemberRecord> CreateTeamMemberRecords(Guid studioRecordId, IEnumerable<StudioTeamMember> teamMembers)
    {
        var displayOrder = 0;
        foreach (var member in teamMembers.Where(HasTeamMemberValue))
        {
            yield return new StudioTeamMemberRecord
            {
                StudioRecordId = studioRecordId,
                DisplayOrder = displayOrder,
                Name = member.Name.Trim(),
                RolesAndResponsibilities = member.RolesAndResponsibilities.Trim(),
                EmailId = string.IsNullOrWhiteSpace(member.EmailId) ? null : member.EmailId.Trim()
            };
            displayOrder++;
        }
    }

    private static IEnumerable<StudioDevelopmentToolsRecord> CreateDevelopmentToolsRecords(Guid studioRecordId, IEnumerable<StudioDevelopmentTool> developmentTools)
    {
        var displayOrder = 0;
        foreach (var tool in developmentTools.Where(HasDevelopmentToolValue))
        {
            yield return new StudioDevelopmentToolsRecord
            {
                StudioRecordId = studioRecordId,
                DisplayOrder = displayOrder,
                Name = tool.Name.Trim(),
                Description = tool.Description.Trim()
            };
            displayOrder++;
        }
    }

    private static IEnumerable<StudioImportantLinkRecord> CreateImportantLinkRecords(Guid studioRecordId, IEnumerable<StudioImportantLink> importantLinks)
    {
        var displayOrder = 0;
        foreach (var link in importantLinks.Where(HasImportantLinkValue))
        {
            yield return new StudioImportantLinkRecord
            {
                StudioRecordId = studioRecordId,
                DisplayOrder = displayOrder,
                Label = link.Label.Trim(),
                Url = link.Url.Trim(),
                Description = link.Description.Trim()
            };
            displayOrder++;
        }
    }

    private static bool HasTeamMemberValue(StudioTeamMember member)
        => !string.IsNullOrWhiteSpace(member.Name)
            || !string.IsNullOrWhiteSpace(member.RolesAndResponsibilities)
            || !string.IsNullOrWhiteSpace(member.EmailId);

    private static bool HasContactValue(StudioContact contact)
        => !string.IsNullOrWhiteSpace(contact.Name)
            || !string.IsNullOrWhiteSpace(contact.Role);

    private static string NormalizeStudioGroup(string? studioGroup) =>
        string.IsNullOrWhiteSpace(studioGroup) ? "Ungrouped" : studioGroup.Trim();

    private static string NormalizeStudioGroupKey(string? studioGroup) =>
        NormalizeStudioGroup(studioGroup).ToUpperInvariant();

    private static int? NormalizeDisplayOrder(int? displayOrder) =>
        displayOrder is > 0 ? displayOrder : null;

    private static string NormalizeTimeZoneId(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc.Id;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim()).Id;
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc.Id;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc.Id;
        }
    }

    private static bool HasImportantLinkValue(StudioImportantLink link)
        => !string.IsNullOrWhiteSpace(link.Label)
            || !string.IsNullOrWhiteSpace(link.Url)
            || !string.IsNullOrWhiteSpace(link.Description);
    private static bool HasDevelopmentToolValue(StudioDevelopmentTool tool)
        => !string.IsNullOrWhiteSpace(tool.Name)
            || !string.IsNullOrWhiteSpace(tool.Description);
}

public static class StudioDirectoryServiceCollectionExtensions
{
    public static IServiceCollection AddStudioDirectory(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(configuration);
        services.AddDbContext<StudioDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("StudioDb") ?? "Data Source=data/studio.db";
            options.UseSqlite(connectionString);
        });
        services.AddScoped<IStudioDatabaseInitializer, SqliteStudioDatabaseInitializer>();
        services.AddScoped<IStudioDirectoryService, SqliteStudioDirectoryService>();
        services.AddScoped<SqliteAtlassianConfigurationService>();
        services.AddScoped<IAtlassianConfigurationService>(provider => provider.GetRequiredService<SqliteAtlassianConfigurationService>());
        services.AddScoped<IAtlassianCredentialAccessor>(provider => provider.GetRequiredService<SqliteAtlassianConfigurationService>());
        services.AddHttpClient<IStudioJiraTicketService, JiraStudioTicketService>();
        services.AddHttpClient<IStudioConfluenceUpdateService, ConfluenceStudioUpdateService>();
        return services;
    }
}
