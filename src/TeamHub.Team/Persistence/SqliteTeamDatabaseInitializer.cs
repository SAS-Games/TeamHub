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
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                """, cancellationToken);

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
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }
}
