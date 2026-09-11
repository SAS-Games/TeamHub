using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TeamHub.Application.Interfaces;
using TeamHub.Domain.Entities;
using TeamHub.Domain.Enums;
using TeamHub.Infrastructure.Persistence;

namespace TeamHub.Infrastructure.Services;

public interface IEmailOutboxProcessor
{
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken = default);
}

public sealed class SmtpNotificationService(
    WorkflowDbContext dbContext,
    IClock clock,
    IEmailNotificationConfigurationService configuration,
    INotificationRecipientResolver recipientResolver,
    ILogger<SmtpNotificationService> logger) : INotificationService
{
    public async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        var settings = await configuration.GetSettingsAsync(cancellationToken);
        var requestedRecipient = message.Type == NotificationType.WorkflowCompleted
            && !string.IsNullOrWhiteSpace(settings.CompletionRecipient)
                ? settings.CompletionRecipient
                : message.Recipient;
        var recipient = await recipientResolver.ResolveEmailAsync(requestedRecipient, cancellationToken);
        var validRecipient = !string.IsNullOrWhiteSpace(recipient) && MailAddress.TryCreate(recipient, out _);
        var item = new NotificationOutboxItem
        {
            WorkflowInstanceId = message.WorkflowInstanceId,
            WorkflowStepInstanceId = message.WorkflowStepInstanceId,
            Type = message.Type,
            Recipient = recipient ?? requestedRecipient,
            Cc = settings.DefaultCc,
            Bcc = settings.DefaultBcc,
            Subject = ApplyTemplate(SubjectTemplate(settings, message.Type), message),
            Body = ApplyTemplate(BodyTemplate(settings, message.Type), message),
            AttachmentPathsJson = JsonSerializer.Serialize(message.AttachmentPaths),
            CreatedAtUtc = clock.UtcNow,
            NextAttemptAtUtc = clock.UtcNow,
            Status = !validRecipient ? "Failed" : settings.Enabled ? "Pending" : "LoggedOnly",
            LastError = !validRecipient
                ? $"No active TeamHub user email or valid email address was found for '{requestedRecipient}'."
                : settings.Enabled ? null : "Email is disabled; notification was recorded only."
        };
        dbContext.NotificationOutbox.Add(item);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (item.Status != "Pending")
        {
            logger.LogInformation("Notification recorded with status {Status}. Recipient={Recipient}; Type={Type}",
                item.Status, item.Recipient, item.Type);
        }
    }

    private static string ApplyTemplate(string template, NotificationMessage message) =>
        template
            .Replace("{Subject}", message.Subject, StringComparison.OrdinalIgnoreCase)
            .Replace("{Body}", message.Body, StringComparison.OrdinalIgnoreCase)
            .Replace("{Recipient}", message.Recipient, StringComparison.OrdinalIgnoreCase);

    private static string SubjectTemplate(EmailNotificationSettings settings, NotificationType type) => type switch
    {
        NotificationType.Assignment => settings.AssignmentSubjectTemplate,
        NotificationType.Reminder or NotificationType.Overdue => settings.ReminderSubjectTemplate,
        NotificationType.Escalation => settings.EscalationSubjectTemplate,
        NotificationType.Completion or NotificationType.WorkflowCompleted => settings.CompletionSubjectTemplate,
        _ => "{Subject}"
    };

    private static string BodyTemplate(EmailNotificationSettings settings, NotificationType type) => type switch
    {
        NotificationType.Assignment => settings.AssignmentBodyTemplate,
        NotificationType.Reminder or NotificationType.Overdue => settings.ReminderBodyTemplate,
        NotificationType.Escalation => settings.EscalationBodyTemplate,
        NotificationType.Completion or NotificationType.WorkflowCompleted => settings.CompletionBodyTemplate,
        _ => "{Body}"
    };
}

internal sealed class SmtpEmailOutboxProcessor(
    WorkflowDbContext dbContext,
    IClock clock,
    IEmailCredentialAccessor credentialAccessor,
    ILogger<SmtpEmailOutboxProcessor> logger) : IEmailOutboxProcessor
{
    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        var (settings, password) = await credentialAccessor.GetDeliverySettingsAsync(cancellationToken);
        if (!settings.Enabled) return 0;

        var now = clock.UtcNow;
        var pending = await dbContext.NotificationOutbox
            .Where(item => (item.Status == "Pending" || item.Status == "Processing")
                && item.NextAttemptAtUtc <= now)
            .OrderBy(item => item.CreatedAtUtc)
            .Take(25)
            .ToListAsync(cancellationToken);
        var sent = 0;
        foreach (var item in pending)
        {
            item.Status = "Processing";
            item.NextAttemptAtUtc = now.AddMinutes(settings.RetryDelayMinutes);
            await dbContext.SaveChangesAsync(cancellationToken);
            try
            {
                using var client = new SmtpClient(settings.Host, settings.Port)
                {
                    EnableSsl = settings.UseSsl,
                    Credentials = new NetworkCredential(settings.Username, password),
                    DeliveryMethod = SmtpDeliveryMethod.Network
                };
                using var mail = new MailMessage
                {
                    From = new MailAddress(settings.FromAddress),
                    Subject = item.Subject,
                    Body = item.Body,
                    IsBodyHtml = false
                };
                mail.To.Add(item.Recipient);
                foreach (var address in EmailNotificationConfigurationService.SplitAddresses(item.Cc)) mail.CC.Add(address);
                foreach (var address in EmailNotificationConfigurationService.SplitAddresses(item.Bcc)) mail.Bcc.Add(address);
                foreach (var path in JsonSerializer.Deserialize<string[]>(item.AttachmentPathsJson) ?? [])
                {
                    if (File.Exists(path)) mail.Attachments.Add(new Attachment(path));
                }

                await client.SendMailAsync(mail, cancellationToken);
                item.Attempts++;
                item.Status = "Sent";
                item.SentAtUtc = clock.UtcNow;
                item.LastError = null;
                sent++;
                AddLog(item, true, null);
                logger.LogInformation("SMTP notification sent to {Recipient} for {Type}", item.Recipient, item.Type);
            }
            catch (Exception ex)
            {
                item.Attempts++;
                item.LastError = ex.Message;
                item.Status = item.Attempts >= settings.MaxDeliveryAttempts ? "Failed" : "Pending";
                item.NextAttemptAtUtc = clock.UtcNow.AddMinutes(settings.RetryDelayMinutes);
                AddLog(item, false, ex.Message);
                logger.LogError(ex, "SMTP notification attempt {Attempt} failed for {Recipient}", item.Attempts, item.Recipient);
            }
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return sent;
    }

    private void AddLog(NotificationOutboxItem item, bool succeeded, string? error) =>
        dbContext.NotificationLogs.Add(new NotificationLog
        {
            WorkflowInstanceId = item.WorkflowInstanceId,
            WorkflowStepInstanceId = item.WorkflowStepInstanceId,
            Type = item.Type,
            Recipient = item.Recipient,
            SentAtUtc = clock.UtcNow,
            Succeeded = succeeded,
            ErrorMessage = error
        });
}
