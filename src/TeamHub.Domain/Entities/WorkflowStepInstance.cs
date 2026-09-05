using TeamHub.Domain.Enums;

namespace TeamHub.Domain.Entities;

public sealed class WorkflowStepInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkflowInstanceId { get; set; }
    public WorkflowInstance WorkflowInstance { get; set; } = null!;

    public Guid? StepDefinitionId { get; set; }
    public string StepKey { get; set; } = string.Empty;
    public string StepName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string OwnerType { get; set; } = "Email";
    public string Owner { get; set; } = string.Empty;
    public WorkflowStepStatus Status { get; set; } = WorkflowStepStatus.Pending;
    public double ExpectedDurationHours { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? CompletedBy { get; set; }
    public bool Required { get; set; }
    public int SortOrder { get; set; }
    public double? ReminderAfterHours { get; set; }
    public double? ReminderRepeatHours { get; set; }
    public double? EscalationAfterHours { get; set; }
    public string? EscalationOwner { get; set; }
    public DateTime? LastReminderAtUtc { get; set; }
    public DateTime? LastEscalationAtUtc { get; set; }

    public ICollection<WorkflowStepInstanceDependency> Dependencies { get; set; } = new List<WorkflowStepInstanceDependency>();
}
