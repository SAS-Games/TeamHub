using Microsoft.Extensions.Logging;
using TeamHub.Application.Interfaces;
using TeamHub.Domain.Entities;
using TeamHub.Infrastructure.Persistence;

namespace TeamHub.Infrastructure.Services;

public sealed class DatabaseNotificationService(WorkflowDbContext dbContext, IClock clock, ILogger<DatabaseNotificationService> logger)
    : INotificationService
{
    public async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation("Notification [{Type}] to {Recipient}: {Subject}", message.Type, message.Recipient, message.Subject);

            dbContext.NotificationLogs.Add(new NotificationLog
            {
                WorkflowInstanceId = message.WorkflowInstanceId,
                WorkflowStepInstanceId = message.WorkflowStepInstanceId,
                Type = message.Type,
                Recipient = message.Recipient,
                SentAtUtc = clock.UtcNow,
                Succeeded = true
            });

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            dbContext.NotificationLogs.Add(new NotificationLog
            {
                WorkflowInstanceId = message.WorkflowInstanceId,
                WorkflowStepInstanceId = message.WorkflowStepInstanceId,
                Type = message.Type,
                Recipient = message.Recipient,
                SentAtUtc = clock.UtcNow,
                Succeeded = false,
                ErrorMessage = ex.Message
            });

            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogError(ex, "Failed to send notification");
        }
    }
}