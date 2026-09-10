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
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
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
