using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using TeamHub.FlowDesigner.Persistence;
using TeamHub.FlowDesigner.Serialization;

namespace TeamHub.FlowDesigner.Tests;

internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"flowdesigner-tests-{Guid.NewGuid():N}.db");
    private readonly string _publishedDatabasePath = Path.Combine(Path.GetTempPath(), $"published-flow-tests-{Guid.NewGuid():N}.db");
    public TestDbContextFactory Factory { get; }
    public PublishedTestDbContextFactory PublishedFactory { get; }

    public TestDatabase()
    {
        var authoringOptions = new DbContextOptionsBuilder<FlowDesignerDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;
        Factory = new TestDbContextFactory(authoringOptions);
        var publishedOptions = new DbContextOptionsBuilder<PublishedFlowDbContext>()
            .UseSqlite($"Data Source={_publishedDatabasePath}")
            .Options;
        PublishedFactory = new PublishedTestDbContextFactory(publishedOptions);
    }

    public async Task<SqliteFlowRepository> CreateRepositoryAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
        return new SqliteFlowRepository(Factory, new SystemTextJsonFlowSerializer());
    }

    public async Task InitializePublishedStoreAsync()
    {
        await using var context = await PublishedFactory.CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        if (File.Exists(_publishedDatabasePath)) File.Delete(_publishedDatabasePath);
        return ValueTask.CompletedTask;
    }
}

internal sealed class TestDbContextFactory(DbContextOptions<FlowDesignerDbContext> options)
    : IDbContextFactory<FlowDesignerDbContext>
{
    public FlowDesignerDbContext CreateDbContext() => new(options);
    public Task<FlowDesignerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}

internal sealed class PublishedTestDbContextFactory(DbContextOptions<PublishedFlowDbContext> options)
    : IDbContextFactory<PublishedFlowDbContext>
{
    public PublishedFlowDbContext CreateDbContext() => new(options);
    public Task<PublishedFlowDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}
