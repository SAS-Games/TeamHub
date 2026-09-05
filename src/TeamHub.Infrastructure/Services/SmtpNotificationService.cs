using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamHub.Application.Interfaces;
using TeamHub.Domain.Entities;
using TeamHub.Infrastructure.Options;
using TeamHub.Infrastructure.Persistence;

namespace TeamHub.Infrastructure.Services;

public sealed class SmtpNotificationService(
    WorkflowDbContext dbContext,
    IClock clock,
    IOptions<EmailNotificationOptions> emailOptions,
    ILogger<SmtpNotificationService> logger) : INotificationService
{
    public async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        var options = emailOptions.Value;

        if (!options.Enabled)
        {
            await LogFallbackAsync(message, options.FallbackToDatabaseLogging, cancellationToken);
            return;
        }

        try
        {
            using var client = new SmtpClient(options.Host, options.Port)
            {
                EnableSsl = options.UseSsl,
                Credentials = new NetworkCredential(options.Username, options.Password),
                DeliveryMethod = SmtpDeliveryMethod.Network
            };

            using var mail = new MailMessage
            {
                From = new MailAddress(options.FromAddress),
                Subject = message.Subject,
                Body = message.Body,
                IsBodyHtml = false
            };

            mail.To.Add(message.Recipient);

            await client.SendMailAsync(mail, cancellationToken);
            logger.LogInformation("SMTP notification sent to {Recipient} for {Type}", message.Recipient, message.Type);

            await PersistLogAsync(message, true, null, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SMTP notification failed for {Recipient}", message.Recipient);
            await PersistLogAsync(message, false, ex.Message, cancellationToken);

            if (options.FallbackToDatabaseLogging)
            {
                await LogFallbackAsync(message, true, cancellationToken);
            }
        }
    }

    private async Task PersistLogAsync(NotificationMessage message, bool succeeded, string? errorMessage, CancellationToken cancellationToken)
    {
        dbContext.NotificationLogs.Add(new NotificationLog
        {
            WorkflowInstanceId = message.WorkflowInstanceId,
            WorkflowStepInstanceId = message.WorkflowStepInstanceId,
            Type = message.Type,
            Recipient = message.Recipient,
            SentAtUtc = clock.UtcNow,
            Succeeded = succeeded,
            ErrorMessage = errorMessage
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task LogFallbackAsync(NotificationMessage message, bool shouldPersist, CancellationToken cancellationToken)
    {
        logger.LogInformation("Email disabled; logging notification only. To={Recipient}; Subject={Subject}", message.Recipient, message.Subject);

        if (shouldPersist)
        {
            await PersistLogAsync(message, true, "SMTP disabled; logged locally only", cancellationToken);
        }
    }
}
