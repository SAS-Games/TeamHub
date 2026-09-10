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
    }
}
