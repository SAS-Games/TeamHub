using TeamHub.Notifications.Models;

namespace TeamHub.Notifications.Services;

public interface IWorkflowNotificationInbox
{
    Task<WorkflowNotificationInbox> GetUnreadAsync(
        string recipient,
        int limit = 10,
        CancellationToken cancellationToken = default);

    Task<WorkflowNotification?> MarkReadAsync(
        Guid notificationId,
        string recipient,
        CancellationToken cancellationToken = default);

    Task<int> MarkAllReadAsync(
        string recipient,
        CancellationToken cancellationToken = default);
}
