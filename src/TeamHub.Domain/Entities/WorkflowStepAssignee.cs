namespace TeamHub.Domain.Entities;

public sealed class WorkflowStepAssignee
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkflowStepInstanceId { get; set; }
    public WorkflowStepInstance WorkflowStepInstance { get; set; } = null!;

    public string Email { get; set; } = string.Empty;
    public DateTime? CompletedAtUtc { get; set; }
    public string? CompletedBy { get; set; }
    public string? CompletionComment { get; set; }
    public DateTime? LastReminderAtUtc { get; set; }
    public DateTime? LastEscalationAtUtc { get; set; }

    public bool IsCompleted => CompletedAtUtc.HasValue;
}
