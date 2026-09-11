using Microsoft.EntityFrameworkCore;

namespace TeamHub.Infrastructure.Persistence;

public interface IWorkflowDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

internal sealed class WorkflowDatabaseInitializer(WorkflowDbContext dbContext) : IWorkflowDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS EmailNotificationSettings (
                Id INTEGER NOT NULL CONSTRAINT PK_EmailNotificationSettings PRIMARY KEY,
                Enabled INTEGER NOT NULL,
                Host TEXT NOT NULL,
                Port INTEGER NOT NULL,
                Username TEXT NOT NULL,
                PasswordProtected TEXT NULL,
                FromAddress TEXT NOT NULL,
                UseSsl INTEGER NOT NULL,
                DefaultCc TEXT NOT NULL,
                DefaultBcc TEXT NOT NULL,
                CompletionRecipient TEXT NOT NULL,
                MaxDeliveryAttempts INTEGER NOT NULL,
                RetryDelayMinutes INTEGER NOT NULL,
                AssignmentSubjectTemplate TEXT NOT NULL,
                AssignmentBodyTemplate TEXT NOT NULL,
                ReminderSubjectTemplate TEXT NOT NULL,
                ReminderBodyTemplate TEXT NOT NULL,
                EscalationSubjectTemplate TEXT NOT NULL,
                EscalationBodyTemplate TEXT NOT NULL,
                CompletionSubjectTemplate TEXT NOT NULL,
                CompletionBodyTemplate TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            INSERT OR IGNORE INTO EmailNotificationSettings (
                Id, Enabled, Host, Port, Username, PasswordProtected, FromAddress, UseSsl,
                DefaultCc, DefaultBcc, CompletionRecipient, MaxDeliveryAttempts, RetryDelayMinutes,
                AssignmentSubjectTemplate, AssignmentBodyTemplate, ReminderSubjectTemplate,
                ReminderBodyTemplate, EscalationSubjectTemplate, EscalationBodyTemplate,
                CompletionSubjectTemplate, CompletionBodyTemplate, UpdatedAtUtc)
            VALUES (
                1, 0, 'smtp.office365.com', 587, '', NULL, '', 1,
                '', '', '', 3, 5, '{{Subject}}', '{{Body}}', '{{Subject}}', '{{Body}}',
                '{{Subject}}', '{{Body}}', '{{Subject}}', '{{Body}}', CURRENT_TIMESTAMP);

            CREATE TABLE IF NOT EXISTS NotificationOutbox (
                Id TEXT NOT NULL CONSTRAINT PK_NotificationOutbox PRIMARY KEY,
                WorkflowInstanceId TEXT NOT NULL,
                WorkflowStepInstanceId TEXT NULL,
                Type INTEGER NOT NULL,
                Recipient TEXT NOT NULL,
                Cc TEXT NOT NULL,
                Bcc TEXT NOT NULL,
                Subject TEXT NOT NULL,
                Body TEXT NOT NULL,
                AttachmentPathsJson TEXT NOT NULL,
                Status TEXT NOT NULL,
                Attempts INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                NextAttemptAtUtc TEXT NOT NULL,
                SentAtUtc TEXT NULL,
                LastError TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_NotificationOutbox_Status_NextAttemptAtUtc
            ON NotificationOutbox (Status, NextAttemptAtUtc);
            """, cancellationToken);
    }
}
