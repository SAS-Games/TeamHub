using TeamHub.AI.Usage.Contracts;
using TeamHub.Domain.Enums;
using TeamHub.Notifications.Services;

namespace TeamHub.Web.AI;

public sealed class TeamHubAiUsageThresholdNotifier(IInAppNotificationPublisher notifications)
    : IAiUsageThresholdNotifier
{
    public Task NotifyAsync(
        AiUsageThresholdNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var reachedLimit = notification.ThresholdPercent >= 100
            || notification.RemainingTokens == 0;
        var subject = reachedLimit
            ? "AI daily token limit reached"
            : $"AI usage reached {notification.ThresholdPercent}%";
        var body = reachedLimit
            ? $"You have used {notification.UsedTokens:N0} of your {notification.DailyTokenLimit:N0} daily AI tokens. Further AI provider calls are blocked until usage resets at {notification.ResetsAtUtc:yyyy-MM-dd HH:mm} UTC."
            : $"You have used {notification.UsedTokens:N0} of your {notification.DailyTokenLimit:N0} daily AI tokens. {notification.RemainingTokens:N0} tokens remain. Usage resets at {notification.ResetsAtUtc:yyyy-MM-dd HH:mm} UTC.";
        return notifications.PublishAsync(new InAppNotificationRequest(
            notification.NotificationId,
            notification.UserId,
            NotificationType.AiUsageThreshold,
            subject,
            body), cancellationToken);
    }
}
