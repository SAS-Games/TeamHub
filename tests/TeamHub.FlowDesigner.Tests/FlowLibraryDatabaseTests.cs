using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.DependencyInjection;
using TeamHub.FlowDesigner.Persistence;

namespace TeamHub.FlowDesigner.Tests;

public sealed class FlowLibraryDatabaseTests
{
    [Fact]
    public async Task Initialize_MigratesLegacyStoresOnceAndPreservesPhysicalTemplateDeletion()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"teamhub-library-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var templatesPath = Path.Combine(directory, "templates.db");
        var publishedPath = Path.Combine(directory, "published-diagrams.db");
        var libraryPath = Path.Combine(directory, "flow-library.db");
        await ExecuteAsync(templatesPath, LegacyTemplateSql);
        await ExecuteAsync(publishedPath, LegacyPublishedSql);

        var services = new ServiceCollection();
        services.AddFlowDesigner(options =>
        {
            options.ConnectionString = $"Data Source={Path.Combine(directory, "flows.db")};Pooling=False";
            options.LibraryConnectionString = $"Data Source={libraryPath};Pooling=False";
            options.LegacyTemplateConnectionString = $"Data Source={templatesPath};Pooling=False";
            options.LegacyPublishedConnectionString = $"Data Source={publishedPath};Pooling=False";
        });
        await using var provider = services.BuildServiceProvider();

        try
        {
            await provider.InitializeFlowDesignerAsync();
            using var scope = provider.CreateScope();
            var templates = scope.ServiceProvider.GetRequiredService<ITemplateCatalogRepository>();
            var migrated = await templates.GetByKeyAsync("LEGACY_TEST");
            Assert.NotNull(migrated);

            var libraryFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FlowLibraryDbContext>>();
            await using (var library = await libraryFactory.CreateDbContextAsync())
            {
                var publishedCount = await library.Database
                    .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM PublishedDiagrams")
                    .SingleAsync();
                Assert.Equal(1, publishedCount);
            }

            await templates.DeleteAsync(migrated!.Id);
            await provider.InitializeFlowDesignerAsync();

            Assert.Null(await templates.GetByKeyAsync("LEGACY_TEST"));
            await using var reloadedLibrary = await libraryFactory.CreateDbContextAsync();
            var storedTemplateCount = await reloadedLibrary.Database
                .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM TemplateDefinitions WHERE TemplateKey = 'LEGACY_TEST'")
                .SingleAsync();
            var deletionMarkerCount = await reloadedLibrary.Database
                .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM DeletedTemplateDefinitions WHERE TemplateKey = 'LEGACY_TEST'")
                .SingleAsync();
            var publishedCountAfterRestart = await reloadedLibrary.Database
                .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM PublishedDiagrams")
                .SingleAsync();
            Assert.Equal(0, storedTemplateCount);
            Assert.Equal(1, deletionMarkerCount);
            Assert.Equal(1, publishedCountAfterRestart);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private const string LegacyTemplateSql = """
        CREATE TABLE TemplateDefinitions (
            Id TEXT NOT NULL PRIMARY KEY,
            TemplateKey TEXT NOT NULL,
            Name TEXT NOT NULL,
            Description TEXT NOT NULL,
            Category TEXT NOT NULL,
            TemplateKind TEXT NOT NULL,
            DiagramType TEXT NULL,
            PayloadJson TEXT NOT NULL,
            Version INTEGER NOT NULL,
            IsActive INTEGER NOT NULL,
            IsBuiltIn INTEGER NOT NULL,
            AdminOnly INTEGER NOT NULL,
            CreatedBy TEXT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        INSERT INTO TemplateDefinitions VALUES (
            '11111111-1111-1111-1111-111111111111',
            'LEGACY_TEST',
            'Legacy test',
            'Migrated template',
            'Tests',
            'FlowDiagram',
            'StandardFlowchart',
            '{}',
            1,
            1,
            0,
            0,
            'tester',
            CURRENT_TIMESTAMP,
            CURRENT_TIMESTAMP
        );
        """;

    private const string LegacyPublishedSql = """
        CREATE TABLE PublishedDiagrams (
            Id TEXT NOT NULL PRIMARY KEY,
            SourceFlowId TEXT NOT NULL,
            PublicationRequestId TEXT NOT NULL,
            Name TEXT NOT NULL,
            Description TEXT NOT NULL,
            DiagramType TEXT NOT NULL,
            NodeCount INTEGER NOT NULL,
            SourceVersion INTEGER NOT NULL,
            PublicationVersion INTEGER NOT NULL,
            GraphJson TEXT NOT NULL,
            SnapshotHash TEXT NOT NULL,
            PublishedAt TEXT NOT NULL,
            PublishedBy TEXT NOT NULL,
            IsCurrent INTEGER NOT NULL
        );
        INSERT INTO PublishedDiagrams VALUES (
            '22222222-2222-2222-2222-222222222222',
            '33333333-3333-3333-3333-333333333333',
            '44444444-4444-4444-4444-444444444444',
            'Published legacy test',
            'Migrated publication',
            'StandardFlowchart',
            1,
            1,
            1,
            '{}',
            'HASH',
            CURRENT_TIMESTAMP,
            'admin',
            1
        );
        """;
}
