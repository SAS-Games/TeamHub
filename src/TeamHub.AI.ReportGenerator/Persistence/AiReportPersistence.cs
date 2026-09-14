using Microsoft.EntityFrameworkCore;

namespace TeamHub.AI.ReportGenerator.Persistence;

public enum AiReportRunStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed record AiReportRunSnapshot(
    Guid Id,
    string ReportType,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string Scope,
    AiReportRunStatus Status,
    string Provider,
    string Model,
    string PromptVersion,
    string SourceHash,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? FailureMessage);

public interface IAiReportRunRepository
{
    Task AddAsync(AiReportRunSnapshot run, CancellationToken cancellationToken = default);
    Task<AiReportRunSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IReportGeneratorDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

internal sealed class AiReportRunRecord
{
    public Guid Id { get; set; }
    public string ReportType { get; set; } = string.Empty;
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public string Scope { get; set; } = string.Empty;
    public AiReportRunStatus Status { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailureMessage { get; set; }
}

internal sealed class AiReportDbContext(DbContextOptions<AiReportDbContext> options) : DbContext(options)
{
    public DbSet<AiReportRunRecord> ReportRuns => Set<AiReportRunRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AiReportRunRecord>(entity =>
        {
            entity.ToTable("AiReportRuns");
            entity.HasKey(run => run.Id);
            entity.Property(run => run.ReportType).HasMaxLength(32);
            entity.Property(run => run.Scope).HasMaxLength(512);
            entity.Property(run => run.Provider).HasMaxLength(128);
            entity.Property(run => run.Model).HasMaxLength(256);
            entity.Property(run => run.PromptVersion).HasMaxLength(64);
            entity.Property(run => run.SourceHash).HasMaxLength(128);
            entity.Property(run => run.CreatedBy).HasMaxLength(320);
            entity.HasIndex(run => new { run.ReportType, run.PeriodStart, run.PeriodEnd });
        });
    }
}

internal sealed class SqliteReportGeneratorDatabaseInitializer(AiReportDbContext dbContext)
    : IReportGeneratorDatabaseInitializer
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        dbContext.Database.EnsureCreatedAsync(cancellationToken);
}

internal sealed class SqliteAiReportRunRepository(AiReportDbContext dbContext) : IAiReportRunRepository
{
    public async Task AddAsync(AiReportRunSnapshot run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        dbContext.ReportRuns.Add(ToRecord(run));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<AiReportRunSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var record = await dbContext.ReportRuns.AsNoTracking()
            .SingleOrDefaultAsync(run => run.Id == id, cancellationToken);
        return record is null ? null : ToSnapshot(record);
    }

    private static AiReportRunRecord ToRecord(AiReportRunSnapshot run) => new()
    {
        Id = run.Id,
        ReportType = run.ReportType,
        PeriodStart = run.PeriodStart,
        PeriodEnd = run.PeriodEnd,
        Scope = run.Scope,
        Status = run.Status,
        Provider = run.Provider,
        Model = run.Model,
        PromptVersion = run.PromptVersion,
        SourceHash = run.SourceHash,
        CreatedBy = run.CreatedBy,
        CreatedAt = run.CreatedAt,
        CompletedAt = run.CompletedAt,
        FailureMessage = run.FailureMessage
    };

    private static AiReportRunSnapshot ToSnapshot(AiReportRunRecord run) => new(
        run.Id,
        run.ReportType,
        run.PeriodStart,
        run.PeriodEnd,
        run.Scope,
        run.Status,
        run.Provider,
        run.Model,
        run.PromptVersion,
        run.SourceHash,
        run.CreatedBy,
        run.CreatedAt,
        run.CompletedAt,
        run.FailureMessage);
}
