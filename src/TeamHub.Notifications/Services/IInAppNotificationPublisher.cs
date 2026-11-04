using TeamHub.Domain.Enums;

namespace TeamHub.Notifications.Services;

public sealed record InAppNotificationRequest(
    Guid Id,
    string Recipient,
    NotificationType Type,
    string Subject,
    string Body);

public interface IInAppNotificationPublisher
{
    Task PublishAsync(
        InAppNotificationRequest request,
        CancellationToken cancellationToken = default);
}
