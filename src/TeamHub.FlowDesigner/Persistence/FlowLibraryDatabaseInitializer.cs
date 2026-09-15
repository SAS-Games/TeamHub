using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace TeamHub.FlowDesigner.Persistence;

internal sealed record FlowLibraryStorageOptions(
    string ConnectionString,
    string? LegacyTemplateConnectionString,
    string? LegacyPublishedConnectionString);

internal sealed class FlowLibraryDatabaseInitializer(
    IDbContextFactory<FlowLibraryDbContext> contextFactory,
    FlowLibraryStorageOptions options)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureMigrationTableAsync(context, cancellationToken);
        await ImportLegacyTemplatesAsync(context, cancellationToken);
        await ImportLegacyPublishedDiagramsAsync(context, cancellationToken);
        await ConvertInactiveTemplatesToDeletionMarkersAsync(context, cancellationToken);
        await UseVersionControlledJournalModeAsync(context, cancellationToken);
    }

    private async Task ImportLegacyTemplatesAsync(
        FlowLibraryDbContext context,
        CancellationToken cancellationToken) =>
        await ImportLegacyDatabaseAsync(
            context,
            options.LegacyTemplateConnectionString,
            "legacy-templates-v1",
            "TemplateDefinitions",
            """
            INSERT OR IGNORE INTO TemplateDefinitions (
                Id, TemplateKey, Name, Description, Category, TemplateKind, DiagramType,
                PayloadJson, Version, IsActive, IsBuiltIn, AdminOnly, CreatedBy, CreatedAt, UpdatedAt)
            SELECT
                Id, TemplateKey, Name, Description, Category, TemplateKind, DiagramType,
                PayloadJson, Version, IsActive, IsBuiltIn, AdminOnly, CreatedBy, CreatedAt, UpdatedAt
            FROM legacy_store.TemplateDefinitions;
            """,
            cancellationToken);

    private async Task ImportLegacyPublishedDiagramsAsync(
        FlowLibraryDbContext context,
        CancellationToken cancellationToken) =>
        await ImportLegacyDatabaseAsync(
            context,
            options.LegacyPublishedConnectionString,
            "legacy-published-diagrams-v1",
            "PublishedDiagrams",
            """
            INSERT OR IGNORE INTO PublishedDiagrams (
                Id, SourceFlowId, PublicationRequestId, Name, Description, DiagramType,
                NodeCount, SourceVersion, PublicationVersion, GraphJson, SnapshotHash,
                PublishedAt, PublishedBy, IsCurrent)
            SELECT
                Id, SourceFlowId, PublicationRequestId, Name, Description, DiagramType,
                NodeCount, SourceVersion, PublicationVersion, GraphJson, SnapshotHash,
                PublishedAt, PublishedBy, IsCurrent
            FROM legacy_store.PublishedDiagrams;
            """,
            cancellationToken);

    private static async Task EnsureMigrationTableAsync(
        FlowLibraryDbContext context,
        CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS FlowLibraryMigrations (
                Name TEXT NOT NULL CONSTRAINT PK_FlowLibraryMigrations PRIMARY KEY,
                AppliedAt TEXT NOT NULL
            );
            """,
            cancellationToken);

    private static async Task ConvertInactiveTemplatesToDeletionMarkersAsync(
        FlowLibraryDbContext context,
        CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT OR IGNORE INTO DeletedTemplateDefinitions (TemplateKey, DeletedAt)
            SELECT TemplateKey, UpdatedAt
            FROM TemplateDefinitions
            WHERE IsActive = 0;

            DELETE FROM TemplateDefinitions
            WHERE IsActive = 0;
            """,
            cancellationToken);
    }

    private static async Task UseVersionControlledJournalModeAsync(
        FlowLibraryDbContext context,
        CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            await using (var checkpoint = connection.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await checkpoint.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var journalMode = connection.CreateCommand();
            journalMode.CommandText = "PRAGMA journal_mode=DELETE;";
            var appliedMode = Convert.ToString(await journalMode.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(appliedMode, "delete", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The Flow Library database could not switch to version-control-safe journal mode.");
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private async Task ImportLegacyDatabaseAsync(
        FlowLibraryDbContext context,
        string? legacyConnectionString,
        string migrationName,
        string requiredTable,
        string importSql,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(legacyConnectionString)) return;

        var legacyPath = ResolveDatabasePath(legacyConnectionString);
        var libraryPath = ResolveDatabasePath(options.ConnectionString);
        if (legacyPath is null
            || libraryPath is null
            || string.Equals(legacyPath, libraryPath, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(legacyPath))
        {
            return;
        }

        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            if (await HasMigrationAsync(connection, migrationName, cancellationToken)) return;

            await using (var attach = connection.CreateCommand())
            {
                attach.CommandText = "ATTACH DATABASE $legacyPath AS legacy_store;";
                attach.Parameters.AddWithValue("$legacyPath", legacyPath);
                await attach.ExecuteNonQueryAsync(cancellationToken);
            }

            try
            {
                if (!await LegacyTableExistsAsync(connection, requiredTable, cancellationToken)) return;

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await using (var import = connection.CreateCommand())
                {
                    import.Transaction = (SqliteTransaction)transaction;
                    import.CommandText = importSql;
                    await import.ExecuteNonQueryAsync(cancellationToken);
                }
                await using (var marker = connection.CreateCommand())
                {
                    marker.Transaction = (SqliteTransaction)transaction;
                    marker.CommandText =
                        "INSERT OR IGNORE INTO FlowLibraryMigrations (Name, AppliedAt) VALUES ($name, $appliedAt);";
                    marker.Parameters.AddWithValue("$name", migrationName);
                    marker.Parameters.AddWithValue("$appliedAt", DateTime.UtcNow);
                    await marker.ExecuteNonQueryAsync(cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
            }
            finally
            {
                await using var detach = connection.CreateCommand();
                detach.CommandText = "DETACH DATABASE legacy_store;";
                await detach.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> HasMigrationAsync(
        SqliteConnection connection,
        string migrationName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM FlowLibraryMigrations WHERE Name = $name;";
        command.Parameters.AddWithValue("$name", migrationName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<bool> LegacyTableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM legacy_store.sqlite_master WHERE type = 'table' AND name = $tableName;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static string? ResolveDatabasePath(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource == ":memory:") return null;
        return Path.GetFullPath(builder.DataSource);
    }
}
