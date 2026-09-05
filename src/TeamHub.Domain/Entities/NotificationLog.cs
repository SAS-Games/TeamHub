using TeamHub.Domain.Enums;

namespace TeamHub.Domain.Entities;

public sealed class NotificationLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkflowInstanceId { get; set; }
    public Guid? WorkflowStepInstanceId { get; set; }
    public NotificationType Type { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public DateTime SentAtUtc { get; set; }
    public bool Succeeded { get; set; }
    public string? ErrorMessage { get; set; }
}
