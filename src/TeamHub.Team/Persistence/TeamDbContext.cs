using Microsoft.EntityFrameworkCore;

namespace TeamHub.Team;

internal sealed class TeamMemberRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EmployeeName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Gid { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string ContactNumber { get; set; } = string.Empty;
    public string Section { get; set; } = TeamMemberSections.TeamMember;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class PageTextAppearanceRecord
{
    public string PageKey { get; set; } = string.Empty;
    public string ColumnKey { get; set; } = string.Empty;
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class SupportSpecializationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Pod { get; set; } = string.Empty;
    public string FocusAreas { get; set; } = string.Empty;
    public string Members { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class TeamAchievementRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string AchievedBy { get; set; } = string.Empty;
    public DateTime AchievedOn { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class TeamDbContext(DbContextOptions<TeamDbContext> options) : DbContext(options)
{
    public DbSet<TeamMemberRecord> TeamMembers => Set<TeamMemberRecord>();
    public DbSet<SupportSpecializationRecord> SupportSpecializations => Set<SupportSpecializationRecord>();
    public DbSet<TeamAchievementRecord> TeamAchievements => Set<TeamAchievementRecord>();
    public DbSet<PageTextAppearanceRecord> PageTextAppearances => Set<PageTextAppearanceRecord>();
    internal DbSet<CustomTeamTabRecord> CustomTeamTabs => Set<CustomTeamTabRecord>();
    internal DbSet<CustomTeamTableRecord> CustomTeamTables => Set<CustomTeamTableRecord>();
    internal DbSet<CustomTeamColumnRecord> CustomTeamColumns => Set<CustomTeamColumnRecord>();
    internal DbSet<CustomTeamRowRecord> CustomTeamRows => Set<CustomTeamRowRecord>();
    internal DbSet<CustomTeamRowAuditRecord> CustomTeamRowAudits => Set<CustomTeamRowAuditRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TeamMemberRecord>(entity =>
        {
            entity.ToTable("TeamMembers");
            entity.Property(x => x.EmployeeName).HasMaxLength(256);
            entity.Property(x => x.Role).HasMaxLength(256);
            entity.Property(x => x.Gid).HasMaxLength(128);
            entity.Property(x => x.Email).HasMaxLength(256);
            entity.Property(x => x.ContactNumber).HasMaxLength(128);
            entity.Property(x => x.Section).HasMaxLength(32);
            entity.HasIndex(x => x.EmployeeName);
        });

        modelBuilder.Entity<SupportSpecializationRecord>(entity =>
        {
            entity.ToTable("SupportSpecializations");
            entity.Property(x => x.Pod).HasMaxLength(256);
            entity.HasIndex(x => x.Pod);
        });

        modelBuilder.Entity<TeamAchievementRecord>(entity =>
        {
            entity.ToTable("TeamAchievements");
            entity.Property(x => x.Title).HasMaxLength(256);
            entity.Property(x => x.Description).HasMaxLength(2000);
            entity.Property(x => x.AchievedBy).HasMaxLength(512);
            entity.HasIndex(x => x.AchievedOn);
        });

        modelBuilder.Entity<PageTextAppearanceRecord>(entity =>
        {
            entity.ToTable("PageTextAppearances");
            entity.HasKey(x => new { x.PageKey, x.ColumnKey });
            entity.Property(x => x.PageKey).HasMaxLength(128);
            entity.Property(x => x.ColumnKey).HasMaxLength(128);
        });

        modelBuilder.Entity<CustomTeamTabRecord>(entity =>
        {
            entity.ToTable("CustomTeamTabs");
            entity.Property(x => x.Name).HasMaxLength(80);
            entity.Property(x => x.Slug).HasMaxLength(80);
            entity.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<CustomTeamTableRecord>(entity =>
        {
            entity.ToTable("CustomTeamTables");
            entity.Property(x => x.Name).HasMaxLength(100);
            entity.Property(x => x.SourceType).HasMaxLength(32);
            entity.Property(x => x.SourceUrl).HasMaxLength(2048);
            entity.Property(x => x.SourceWorksheet).HasMaxLength(200);
            entity.Property(x => x.PrimaryKeySourceHeader).HasMaxLength(200);
            entity.Property(x => x.LastSyncStatus).HasMaxLength(32);
            entity.Property(x => x.LastSyncMessage).HasMaxLength(1000);
            entity.HasIndex(x => new { x.TabId, x.DisplayOrder });
            entity.HasOne<CustomTeamTabRecord>().WithMany().HasForeignKey(x => x.TabId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CustomTeamColumnRecord>(entity =>
        {
            entity.ToTable("CustomTeamColumns");
            entity.Property(x => x.Key).HasMaxLength(80);
            entity.Property(x => x.Label).HasMaxLength(80);
            entity.Property(x => x.FieldType).HasMaxLength(32);
            entity.Property(x => x.SourceHeader).HasMaxLength(200);
            entity.HasIndex(x => new { x.TableId, x.Key }).IsUnique();
            entity.HasOne<CustomTeamTableRecord>().WithMany().HasForeignKey(x => x.TableId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CustomTeamRowRecord>(entity =>
        {
            entity.ToTable("CustomTeamRows");
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.CreatedBy).HasMaxLength(256);
            entity.Property(x => x.UpdatedBy).HasMaxLength(256);
            entity.Property(x => x.DeletedBy).HasMaxLength(256);
            entity.Property(x => x.SourceKey).HasMaxLength(512);
            entity.Property(x => x.SourceStatus).HasMaxLength(16);
            entity.HasIndex(x => new { x.TableId, x.IsDeleted, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.TableId, x.SourceKey }).IsUnique().HasFilter("SourceKey IS NOT NULL AND IsDeleted = 0");
            entity.HasOne<CustomTeamTableRecord>().WithMany().HasForeignKey(x => x.TableId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CustomTeamRowAuditRecord>(entity =>
        {
            entity.ToTable("CustomTeamRowAudits");
            entity.Property(x => x.Action).HasMaxLength(16);
            entity.Property(x => x.Actor).HasMaxLength(256);
            entity.HasIndex(x => new { x.RowId, x.CreatedAtUtc });
            entity.HasOne<CustomTeamRowRecord>().WithMany().HasForeignKey(x => x.RowId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
