using Microsoft.EntityFrameworkCore;
using System.Data;

namespace TeamHub.MilestoneReview;

internal sealed class MilestoneReviewRecord
{
    public Guid Id { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string MilestoneName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? JiraTicketId { get; set; }
    public MilestoneReviewStatus Status { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}

internal sealed class MilestoneReviewAssignmentRecord
{
    public Guid ReviewId { get; set; }
    public Guid ReviewerId { get; set; }
    public string Reviewer { get; set; } = string.Empty;
    public string ReviewerUserId { get; set; } = string.Empty;
}

internal sealed class MilestoneReviewFeedbackRecord
{
    public Guid Id { get; set; }
    public Guid ReviewId { get; set; }
    public Guid ReviewerId { get; set; }
    public string ReviewerUserId { get; set; } = string.Empty;
    public string Reviewer { get; set; } = string.Empty;
    public string WhatGood { get; set; } = string.Empty;
    public string WhatsBad { get; set; } = string.Empty;
    public string NeedImprovements { get; set; } = string.Empty;
    public int Version { get; set; }
    public string? JiraCommentId { get; set; }
    public string? JiraSyncError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class MilestoneReviewDbContext(DbContextOptions<MilestoneReviewDbContext> options) : DbContext(options)
{
    internal DbSet<MilestoneReviewRecord> Reviews => Set<MilestoneReviewRecord>();
    internal DbSet<MilestoneReviewAssignmentRecord> Assignments => Set<MilestoneReviewAssignmentRecord>();
    internal DbSet<MilestoneReviewFeedbackRecord> Feedback => Set<MilestoneReviewFeedbackRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MilestoneReviewRecord>(entity =>
        {
            entity.ToTable("MilestoneReviews");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.ProjectName).HasMaxLength(160).IsRequired();
            entity.Property(item => item.MilestoneName).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(4000).IsRequired();
            entity.Property(item => item.JiraTicketId).HasMaxLength(64);
            entity.Property(item => item.CreatedBy).HasMaxLength(320).IsRequired();
            entity.HasIndex(item => item.Status).IsUnique().HasFilter("\"Status\" IN (0, 1)");
        });
        modelBuilder.Entity<MilestoneReviewAssignmentRecord>(entity =>
        {
            entity.ToTable("MilestoneReviewAssignments");
            entity.HasKey(item => new { item.ReviewId, item.ReviewerId });
            entity.Property(item => item.Reviewer).HasMaxLength(160).IsRequired();
            entity.Property(item => item.ReviewerUserId).HasMaxLength(320).IsRequired();
            entity.HasOne<MilestoneReviewRecord>().WithMany()
                .HasForeignKey(item => item.ReviewId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<MilestoneReviewFeedbackRecord>(entity =>
        {
            entity.ToTable("MilestoneReviewFeedback");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Reviewer).HasMaxLength(160).IsRequired();
            entity.Property(item => item.ReviewerUserId).HasMaxLength(320).IsRequired();
            entity.Property(item => item.WhatGood).HasMaxLength(20000).IsRequired();
            entity.Property(item => item.WhatsBad).HasMaxLength(20000).IsRequired();
            entity.Property(item => item.NeedImprovements).HasMaxLength(20000).IsRequired();
            entity.Property(item => item.JiraCommentId).HasMaxLength(32);
            entity.Property(item => item.JiraSyncError).HasMaxLength(1000);
            entity.Property(item => item.Version).IsConcurrencyToken();
            entity.HasIndex(item => new { item.ReviewId, item.ReviewerId }).IsUnique();
            entity.HasOne<MilestoneReviewRecord>().WithMany()
                .HasForeignKey(item => item.ReviewId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public interface IMilestoneReviewDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

internal sealed class SqliteMilestoneReviewDatabaseInitializer(MilestoneReviewDbContext dbContext)
    : IMilestoneReviewDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        await AddColumnIfMissingAsync("MilestoneReviews", "JiraTicketId", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync("MilestoneReviewAssignments", "ReviewerUserId", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await AddColumnIfMissingAsync("MilestoneReviewFeedback", "ReviewerUserId", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await AddColumnIfMissingAsync("MilestoneReviewFeedback", "JiraCommentId", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync("MilestoneReviewFeedback", "JiraSyncError", "TEXT NULL", cancellationToken);
    }

    private async Task AddColumnIfMissingAsync(
        string table, string column, string definition, CancellationToken cancellationToken)
    {
        if (!table.All(char.IsAsciiLetter) || !column.All(char.IsAsciiLetter))
            throw new ArgumentException("Database schema identifiers must contain only ASCII letters.");
        var connection = dbContext.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen) await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var columnExists = false;
            await using (var inspectCommand = connection.CreateCommand())
            {
                inspectCommand.CommandText = $"PRAGMA table_info(\"{table}\")";
                await using var reader = await inspectCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    {
                        columnExists = true;
                        break;
                    }
                }
            }
            if (columnExists) return;

            await using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
            await alterCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (!wasOpen) await dbContext.Database.CloseConnectionAsync();
        }
    }
}
