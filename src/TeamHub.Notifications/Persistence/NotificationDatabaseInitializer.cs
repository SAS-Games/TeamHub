using Microsoft.EntityFrameworkCore;

namespace TeamHub.Notifications.Persistence;

public interface INotificationDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

internal sealed class NotificationDatabaseInitializer(NotificationsDbContext dbContext)
    : INotificationDatabaseInitializer
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        dbContext.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS NotificationReadReceipts (
                NotificationId TEXT NOT NULL CONSTRAINT PK_NotificationReadReceipts PRIMARY KEY,
                Recipient TEXT COLLATE NOCASE NOT NULL,
                ReadAtUtc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_NotificationReadReceipts_Recipient_ReadAtUtc
            ON NotificationReadReceipts (Recipient, ReadAtUtc);
            """, cancellationToken);
}
