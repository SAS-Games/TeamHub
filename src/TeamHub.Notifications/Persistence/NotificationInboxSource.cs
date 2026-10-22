using TeamHub.Domain.Enums;

namespace TeamHub.Notifications.Persistence;

internal sealed class NotificationInboxSource
{
    public Guid Id { get; set; }
    public Guid WorkflowInstanceId { get; set; }
    public Guid? WorkflowStepInstanceId { get; set; }
    public NotificationType Type { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
