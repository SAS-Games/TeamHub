using Microsoft.EntityFrameworkCore;

namespace TeamHub.Team;

internal sealed class SqliteTeamDatabaseInitializer(TeamDbContext dbContext) : ITeamDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS TeamMembers (
                    Id TEXT NOT NULL CONSTRAINT PK_TeamMembers PRIMARY KEY,
                    EmployeeName TEXT NOT NULL,
                    Role TEXT NOT NULL,
                    Gid TEXT NOT NULL,
                    Email TEXT NOT NULL,
                    ContactNumber TEXT NOT NULL,
                    Section TEXT NOT NULL DEFAULT 'TeamMember',
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                """, cancellationToken);

            if (!await ColumnExistsAsync("TeamMembers", "Section", cancellationToken))
            {
                await dbContext.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE TeamMembers
                    ADD COLUMN Section TEXT NOT NULL DEFAULT 'TeamMember';
                    """, cancellationToken);
            }

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS IX_TeamMembers_EmployeeName
                ON TeamMembers (EmployeeName);
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS SupportSpecializations (
                    Id TEXT NOT NULL CONSTRAINT PK_SupportSpecializations PRIMARY KEY,
                    Pod TEXT NOT NULL,
                    FocusAreas TEXT NOT NULL,
                    Members TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS IX_SupportSpecializations_Pod
                ON SupportSpecializations (Pod);
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS TeamAchievements (
                    Id TEXT NOT NULL CONSTRAINT PK_TeamAchievements PRIMARY KEY,
                    Title TEXT NOT NULL,
                    Description TEXT NOT NULL,
                    AchievedBy TEXT NOT NULL,
                    AchievedOn TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS IX_TeamAchievements_AchievedOn
                ON TeamAchievements (AchievedOn);
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS PageTextAppearances (
                    PageKey TEXT NOT NULL,
                    ColumnKey TEXT NOT NULL,
                    IsBold INTEGER NOT NULL,
                    IsItalic INTEGER NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    CONSTRAINT PK_PageTextAppearances PRIMARY KEY (PageKey, ColumnKey)
                );
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS CustomTeamTabs (
                    Id TEXT NOT NULL CONSTRAINT PK_CustomTeamTabs PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Slug TEXT NOT NULL,
                    DisplayOrder INTEGER NOT NULL,
                    IsArchived INTEGER NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_CustomTeamTabs_Slug ON CustomTeamTabs (Slug);

                CREATE TABLE IF NOT EXISTS CustomTeamTables (
                    Id TEXT NOT NULL CONSTRAINT PK_CustomTeamTables PRIMARY KEY,
                    TabId TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    DisplayOrder INTEGER NOT NULL,
                    IsArchived INTEGER NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    CONSTRAINT FK_CustomTeamTables_CustomTeamTabs_TabId
                        FOREIGN KEY (TabId) REFERENCES CustomTeamTabs (Id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_CustomTeamTables_TabId_DisplayOrder
                    ON CustomTeamTables (TabId, DisplayOrder);

                CREATE TABLE IF NOT EXISTS CustomTeamColumns (
                    Id TEXT NOT NULL CONSTRAINT PK_CustomTeamColumns PRIMARY KEY,
                    TableId TEXT NOT NULL,
                    Key TEXT NOT NULL,
                    Label TEXT NOT NULL,
                    FieldType TEXT NOT NULL,
                    IsRequired INTEGER NOT NULL,
                    OptionsJson TEXT NOT NULL,
                    DisplayOrder INTEGER NOT NULL,
                    IsArchived INTEGER NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    CONSTRAINT FK_CustomTeamColumns_CustomTeamTables_TableId
                        FOREIGN KEY (TableId) REFERENCES CustomTeamTables (Id) ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_CustomTeamColumns_TableId_Key
                    ON CustomTeamColumns (TableId, Key);

                CREATE TABLE IF NOT EXISTS CustomTeamRows (
                    Id TEXT NOT NULL CONSTRAINT PK_CustomTeamRows PRIMARY KEY,
                    TableId TEXT NOT NULL,
                    ValuesJson TEXT NOT NULL,
                    Version INTEGER NOT NULL,
                    IsDeleted INTEGER NOT NULL,
                    CreatedBy TEXT NOT NULL,
                    UpdatedBy TEXT NOT NULL,
                    DeletedBy TEXT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    DeletedAtUtc TEXT NULL,
                    CONSTRAINT FK_CustomTeamRows_CustomTeamTables_TableId
                        FOREIGN KEY (TableId) REFERENCES CustomTeamTables (Id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_CustomTeamRows_TableId_IsDeleted_CreatedAtUtc
                    ON CustomTeamRows (TableId, IsDeleted, CreatedAtUtc);

                CREATE TABLE IF NOT EXISTS CustomTeamRowAudits (
                    Id TEXT NOT NULL CONSTRAINT PK_CustomTeamRowAudits PRIMARY KEY,
                    RowId TEXT NOT NULL,
                    TableId TEXT NOT NULL,
                    Action TEXT NOT NULL,
                    ValuesJson TEXT NOT NULL,
                    Version INTEGER NOT NULL,
                    Actor TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    CONSTRAINT FK_CustomTeamRowAudits_CustomTeamRows_RowId
                        FOREIGN KEY (RowId) REFERENCES CustomTeamRows (Id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_CustomTeamRowAudits_RowId_CreatedAtUtc
                    ON CustomTeamRowAudits (RowId, CreatedAtUtc);
                """, cancellationToken);

            await AddColumnIfMissingAsync("CustomTeamTables", "SourceType", "TEXT NOT NULL DEFAULT 'Manual'", cancellationToken);
            await AddColumnIfMissingAsync("CustomTeamTables", "SourceUrl", "TEXT NULL", cancellationToken);
            await AddColumnIfMissingAsync("CustomTeamTables", "SourceWorksheet", "TEXT NULL", cancellationToken);
            await AddColumnIfMissingAsync("CustomTeamTables", "SourceHeaderRow", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
            await AddColumnIfMissingAsync("CustomTeamTables", "PrimaryKeySourceHeader", "TEXT NULL", cancellationToken);
            await AddColumnIfMissingAsync("CustomTeamTables", "LastSyncedAtUtc", "TEXT NULL", cancellationToken);
            await AddColumnIfMissingAsync("CustomTeamTables", "LastSyncStatus", "TEXT NULL", cancellationToken);
            await AddColumnIfMissingAsync("CustomTeamTables", "LastSyncMessage", "TEXT NULL", cancellationToken);
            await AddColumnIfMissingAsync("CustomTeamColumns", "IsSourceColumn", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await AddColumnIfMissingAsync("CustomTeamColumns", "SourceHeader", "TEXT NULL", cancellationToken);
            await AddColumnIfMissingAsync("CustomTeamRows", "SourceKey", "TEXT NULL", cancellationToken);
            await AddColumnIfMissingAsync("CustomTeamRows", "SourceStatus", "TEXT NOT NULL DEFAULT 'Manual'", cancellationToken);
            await AddColumnIfMissingAsync("CustomTeamRows", "LastSeenAtUtc", "TEXT NULL", cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE UNIQUE INDEX IF NOT EXISTS IX_CustomTeamRows_TableId_SourceKey
                    ON CustomTeamRows (TableId, SourceKey)
                    WHERE SourceKey IS NOT NULL AND IsDeleted = 0;
                """, cancellationToken);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private async Task AddColumnIfMissingAsync(
        string tableName,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        if (!await ColumnExistsAsync(tableName, columnName, cancellationToken))
        {
            #pragma warning disable EF1002 // Schema names and definitions are fixed internal constants.

            await dbContext.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};",
                cancellationToken);

            #pragma warning restore EF1002
        }
    }

    private async Task<bool> ColumnExistsAsync(
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
