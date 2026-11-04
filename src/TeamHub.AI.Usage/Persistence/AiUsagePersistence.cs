using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamHub.AI.Usage.Contracts;

namespace TeamHub.AI.Usage.Persistence;

internal sealed class AiUsageOperationRecord
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string NormalizedUserId { get; set; } = string.Empty;
    public string Feature { get; set; } = string.Empty;
    public string Subfeature { get; set; } = string.Empty;
    public string Application { get; set; } = string.Empty;
    public AiUsageExecutionType ExecutionType { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public List<AiUsageInvocationRecord> Invocations { get; set; } = [];
    public List<AiToolActivityRecord> ToolActivities { get; set; } = [];
}

internal sealed class AiUsageInvocationRecord
{
    public Guid Id { get; set; }
    public Guid OperationId { get; set; }
    public AiUsageOperationRecord Operation { get; set; } = null!;
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? ProviderRequestId { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? TotalTokens { get; set; }
    public AiUsageMeasurementSource MeasurementSource { get; set; }
    public AiUsageInvocationStatus Status { get; set; }
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public long DurationMilliseconds { get; set; }
    public string? ErrorType { get; set; }
}

internal sealed class AiToolActivityRecord
{
    public Guid Id { get; set; }
    public Guid OperationId { get; set; }
    public AiUsageOperationRecord Operation { get; set; } = null!;
    public string ToolName { get; set; } = string.Empty;
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public string? ExternalSource { get; set; }
    public AiToolActivityStatus Status { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public long DurationMilliseconds { get; set; }
    public string? ErrorType { get; set; }
}

internal sealed class AiUsageLimitSettingsRecord
{
    public int Id { get; set; }
    public long? DefaultDailyTokenLimit { get; set; }
    public string ResetTimeZoneId { get; set; } = "Asia/Kolkata";
}

internal sealed class AiUsageUserLimitRecord
{
    public string NormalizedUserId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public long DailyTokenLimit { get; set; }
}

internal sealed class AiUsageDbContext(DbContextOptions<AiUsageDbContext> options) : DbContext(options)
{
    public DbSet<AiUsageOperationRecord> Operations => Set<AiUsageOperationRecord>();
    public DbSet<AiUsageInvocationRecord> Invocations => Set<AiUsageInvocationRecord>();
    public DbSet<AiToolActivityRecord> ToolActivities => Set<AiToolActivityRecord>();
    public DbSet<AiUsageLimitSettingsRecord> LimitSettings => Set<AiUsageLimitSettingsRecord>();
    public DbSet<AiUsageUserLimitRecord> UserLimits => Set<AiUsageUserLimitRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AiUsageOperationRecord>(entity =>
        {
            entity.ToTable("AiUsageOperations");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.UserId).HasMaxLength(320).IsRequired();
            entity.Property(item => item.NormalizedUserId).HasMaxLength(320).IsRequired();
            entity.Property(item => item.Feature).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Subfeature).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Application).HasMaxLength(160).IsRequired();
            entity.HasIndex(item => item.StartedAtUtc);
            entity.HasIndex(item => new { item.NormalizedUserId, item.StartedAtUtc });
            entity.HasIndex(item => new { item.Feature, item.StartedAtUtc });
            entity.HasMany(item => item.Invocations)
                .WithOne(item => item.Operation)
                .HasForeignKey(item => item.OperationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(item => item.ToolActivities)
                .WithOne(item => item.Operation)
                .HasForeignKey(item => item.OperationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiToolActivityRecord>(entity =>
        {
            entity.ToTable("AiToolActivities");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.ToolName).HasMaxLength(64).IsRequired();
            entity.Property(item => item.ResourceType).HasMaxLength(100);
            entity.Property(item => item.ResourceId).HasMaxLength(256);
            entity.Property(item => item.ExternalSource).HasMaxLength(100);
            entity.Property(item => item.ErrorType).HasMaxLength(256);
            entity.HasIndex(item => item.OperationId);
            entity.HasIndex(item => item.StartedAtUtc);
            entity.HasIndex(item => new { item.ToolName, item.StartedAtUtc });
        });

        modelBuilder.Entity<AiUsageInvocationRecord>(entity =>
        {
            entity.ToTable("AiUsageInvocations");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Provider).HasMaxLength(128).IsRequired();
            entity.Property(item => item.Model).HasMaxLength(256).IsRequired();
            entity.Property(item => item.ProviderRequestId).HasMaxLength(256);
            entity.Property(item => item.ResourceType).HasMaxLength(100);
            entity.Property(item => item.ResourceId).HasMaxLength(256);
            entity.Property(item => item.ErrorType).HasMaxLength(256);
            entity.HasIndex(item => item.OperationId);
            entity.HasIndex(item => item.StartedAtUtc);
            entity.HasIndex(item => new { item.Provider, item.Model, item.StartedAtUtc });
        });

        modelBuilder.Entity<AiUsageLimitSettingsRecord>(entity =>
        {
            entity.ToTable("AiUsageLimitSettings");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.ResetTimeZoneId).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<AiUsageUserLimitRecord>(entity =>
        {
            entity.ToTable("AiUsageUserLimits");
            entity.HasKey(item => item.NormalizedUserId);
            entity.Property(item => item.NormalizedUserId).HasMaxLength(320);
            entity.Property(item => item.UserId).HasMaxLength(320).IsRequired();
        });
    }
}

internal sealed class AiUsageDatabaseInitializer(
    AiUsageDbContext dbContext,
    IOptions<AiUsageOptions> options,
    TimeProvider timeProvider)
    : IAiUsageDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS AiUsageLimitSettings (
                Id INTEGER NOT NULL CONSTRAINT PK_AiUsageLimitSettings PRIMARY KEY,
                DefaultDailyTokenLimit INTEGER NULL,
                ResetTimeZoneId TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AiUsageUserLimits (
                NormalizedUserId TEXT NOT NULL CONSTRAINT PK_AiUsageUserLimits PRIMARY KEY,
                UserId TEXT NOT NULL,
                DailyTokenLimit INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AiToolActivities (
                Id TEXT NOT NULL CONSTRAINT PK_AiToolActivities PRIMARY KEY,
                OperationId TEXT NOT NULL,
                ToolName TEXT NOT NULL,
                ResourceType TEXT NULL,
                ResourceId TEXT NULL,
                ExternalSource TEXT NULL,
                Status INTEGER NOT NULL,
                StartedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NOT NULL,
                DurationMilliseconds INTEGER NOT NULL,
                ErrorType TEXT NULL,
                CONSTRAINT FK_AiToolActivities_AiUsageOperations_OperationId
                    FOREIGN KEY (OperationId) REFERENCES AiUsageOperations (Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_AiToolActivities_OperationId
                ON AiToolActivities (OperationId);
            CREATE INDEX IF NOT EXISTS IX_AiToolActivities_StartedAtUtc
                ON AiToolActivities (StartedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_AiToolActivities_ToolName_StartedAtUtc
                ON AiToolActivities (ToolName, StartedAtUtc);

            INSERT OR IGNORE INTO AiUsageLimitSettings (
                Id, DefaultDailyTokenLimit, ResetTimeZoneId)
            VALUES (1, NULL, 'Asia/Kolkata');
            """, cancellationToken);
        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddMonths(-options.Value.RetentionMonths);
        await dbContext.Operations
            .Where(item => item.StartedAtUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
