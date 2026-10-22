using Microsoft.EntityFrameworkCore;
using TeamHub.Application.Models;
using TeamHub.Domain.Entities;

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

            CREATE TABLE IF NOT EXISTS AiConfiguration (
                Id INTEGER NOT NULL CONSTRAINT PK_AiConfiguration PRIMARY KEY,
                SettingsJson TEXT NOT NULL,
                CredentialProtected TEXT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                UpdatedBy TEXT NOT NULL
            );

            INSERT OR IGNORE INTO EmailNotificationSettings (
                Id, Enabled, Host, Port, Username, PasswordProtected, FromAddress, UseSsl,
                DefaultCc, DefaultBcc, CompletionRecipient, MaxDeliveryAttempts, RetryDelayMinutes,
                AssignmentSubjectTemplate, AssignmentBodyTemplate, ReminderSubjectTemplate,
                ReminderBodyTemplate, EscalationSubjectTemplate, EscalationBodyTemplate,
                CompletionSubjectTemplate, CompletionBodyTemplate, UpdatedAtUtc)
            VALUES (
                1, 0, 'mrelay.noc.sony.co.jp', 25, '', NULL, '', 0,
                '', '', '', 3, 5, '{{Subject}}', '{{Body}}', '{{Subject}}', '{{Body}}',
                '{{Subject}}', '{{Body}}', '{{Subject}}', '{{Body}}', CURRENT_TIMESTAMP);

            UPDATE EmailNotificationSettings
            SET Host = 'mrelay.noc.sony.co.jp',
                Port = 25,
                Username = '',
                PasswordProtected = NULL,
                UseSsl = 0,
                UpdatedAtUtc = CURRENT_TIMESTAMP
            WHERE lower(trim(Host)) = 'smtp.office365.com'
                AND Port = 587;

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

            CREATE TABLE IF NOT EXISTS WorkflowStepAssignees (
                Id TEXT NOT NULL CONSTRAINT PK_WorkflowStepAssignees PRIMARY KEY,
                WorkflowStepInstanceId TEXT NOT NULL,
                Email TEXT COLLATE NOCASE NOT NULL,
                CompletedAtUtc TEXT NULL,
                CompletedBy TEXT NULL,
                CompletionComment TEXT NULL,
                LastReminderAtUtc TEXT NULL,
                LastEscalationAtUtc TEXT NULL,
                CONSTRAINT FK_WorkflowStepAssignees_WorkflowStepInstances_WorkflowStepInstanceId
                    FOREIGN KEY (WorkflowStepInstanceId) REFERENCES WorkflowStepInstances (Id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorkflowStepAssignees_WorkflowStepInstanceId_Email
            ON WorkflowStepAssignees (WorkflowStepInstanceId, Email);
            """, cancellationToken);

        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await AddEscalationColumnsAsync("WorkflowStepDefinitions", includeSentTimestamps: false, cancellationToken);
            await AddEscalationColumnsAsync("WorkflowStepInstances", includeSentTimestamps: true, cancellationToken);
            await AddEscalationColumnsAsync("WorkflowDraftStepDefinitions", includeSentTimestamps: false, cancellationToken);
            await AddNotificationTemplateColumnsAsync("WorkflowStepDefinitions", cancellationToken);
            await AddNotificationTemplateColumnsAsync("WorkflowStepInstances", cancellationToken);
            await AddNotificationTemplateColumnsAsync("WorkflowDraftStepDefinitions", cancellationToken);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }

        var stepsWithoutAssignees = await dbContext.WorkflowStepInstances
            .Include(step => step.Assignees)
            .Where(step => !step.Assignees.Any())
            .ToListAsync(cancellationToken);

        var legacyAssignees = new List<WorkflowStepAssignee>();
        foreach (var step in stepsWithoutAssignees)
        {
            foreach (var email in WorkflowAssigneeEmails.Parse(step.Owner))
            {
                legacyAssignees.Add(new WorkflowStepAssignee
                {
                    WorkflowStepInstanceId = step.Id,
                    Email = email
                });
            }
        }

        if (legacyAssignees.Count > 0)
        {
            dbContext.WorkflowStepAssignees.AddRange(legacyAssignees);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task AddNotificationTemplateColumnsAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(tableName, cancellationToken))
        {
            return;
        }

        await AddColumnIfMissingAsync(tableName, "ReminderSubjectTemplate", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(tableName, "ReminderBodyTemplate", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(tableName, "AssignmentSubjectTemplate", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(tableName, "AssignmentBodyTemplate", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(tableName, "OverdueSubjectTemplate", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(tableName, "OverdueBodyTemplate", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(tableName, "EscalationSubjectTemplate", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(tableName, "EscalationBodyTemplate", "TEXT NULL", cancellationToken);
    }

    private async Task AddEscalationColumnsAsync(
        string tableName,
        bool includeSentTimestamps,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(tableName, cancellationToken))
        {
            return;
        }

        await AddColumnIfMissingAsync(tableName, "EscalationLevel2AfterHours", "REAL NULL", cancellationToken);
        await AddColumnIfMissingAsync(tableName, "EscalationLevel2Owners", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(tableName, "EscalationLevel3AfterHours", "REAL NULL", cancellationToken);
        await AddColumnIfMissingAsync(tableName, "EscalationLevel3Owners", "TEXT NULL", cancellationToken);
        if (includeSentTimestamps)
        {
            await AddColumnIfMissingAsync(tableName, "LastEscalationLevel2AtUtc", "TEXT NULL", cancellationToken);
            await AddColumnIfMissingAsync(tableName, "LastEscalationLevel3AtUtc", "TEXT NULL", cancellationToken);
        }
    }

    private async Task AddColumnIfMissingAsync(
        string tableName,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        if (await ColumnExistsAsync(tableName, columnName, cancellationToken))
        {
            return;
        }

        #pragma warning disable EF1002 // Table and column names are fixed internal constants.
        await dbContext.Database.ExecuteSqlRawAsync(
            $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};",
            cancellationToken);
        #pragma warning restore EF1002
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

    private async Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
