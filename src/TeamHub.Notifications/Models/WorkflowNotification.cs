using TeamHub.Domain.Enums;

namespace TeamHub.Notifications.Models;

public sealed record WorkflowNotification(
    Guid Id,
    Guid WorkflowInstanceId,
    Guid? WorkflowStepInstanceId,
    NotificationType Type,
    string Subject,
    string BodyPreview,
    DateTime CreatedAtUtc);

public sealed record WorkflowNotificationInbox(
    int UnreadCount,
    IReadOnlyList<WorkflowNotification> Notifications);
