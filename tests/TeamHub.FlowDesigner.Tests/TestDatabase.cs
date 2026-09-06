using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using TeamHub.FlowDesigner.Persistence;
using TeamHub.FlowDesigner.Serialization;

namespace TeamHub.FlowDesigner.Tests;

internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"flowdesigner-tests-{Guid.NewGuid():N}.db");
    public TestDbContextFactory Factory { get; }

    public TestDatabase()
    {
        var options = new DbContextOptionsBuilder<FlowDesignerDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;
        Factory = new TestDbContextFactory(options);
    }

    public async Task<SqliteFlowRepository> CreateRepositoryAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
        return new SqliteFlowRepository(Factory, new SystemTextJsonFlowSerializer());
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
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
