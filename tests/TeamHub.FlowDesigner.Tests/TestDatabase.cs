using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using TeamHub.FlowDesigner.Persistence;
using TeamHub.FlowDesigner.Serialization;

namespace TeamHub.FlowDesigner.Tests;

internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"flowdesigner-tests-{Guid.NewGuid():N}.db");
    private readonly string _libraryDatabasePath = Path.Combine(Path.GetTempPath(), $"flow-library-tests-{Guid.NewGuid():N}.db");
    public TestDbContextFactory Factory { get; }
    public LibraryTestDbContextFactory LibraryFactory { get; }

    public TestDatabase()
    {
        var authoringOptions = new DbContextOptionsBuilder<FlowDesignerDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;
        Factory = new TestDbContextFactory(authoringOptions);
        var libraryOptions = new DbContextOptionsBuilder<FlowLibraryDbContext>()
            .UseSqlite($"Data Source={_libraryDatabasePath}")
            .Options;
        LibraryFactory = new LibraryTestDbContextFactory(libraryOptions);
    }

    public async Task<SqliteFlowRepository> CreateRepositoryAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
        return new SqliteFlowRepository(Factory, new SystemTextJsonFlowSerializer());
    }

    public async Task InitializeLibraryStoreAsync()
    {
        await using var context = await LibraryFactory.CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        if (File.Exists(_libraryDatabasePath)) File.Delete(_libraryDatabasePath);
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

internal sealed class LibraryTestDbContextFactory(DbContextOptions<FlowLibraryDbContext> options)
    : IDbContextFactory<FlowLibraryDbContext>
{
    public FlowLibraryDbContext CreateDbContext() => new(options);
    public Task<FlowLibraryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}
