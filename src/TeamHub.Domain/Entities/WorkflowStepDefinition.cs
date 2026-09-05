namespace TeamHub.Domain.Entities;

public sealed class WorkflowStepDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkflowDefinitionId { get; set; }
    public WorkflowDefinition WorkflowDefinition { get; set; } = null!;

    public string StepKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string OwnerType { get; set; } = "Email";
    public string Owner { get; set; } = string.Empty;
    public double ExpectedDurationHours { get; set; }
    public double? ReminderAfterHours { get; set; }
    public double? ReminderRepeatHours { get; set; }
    public double? EscalationAfterHours { get; set; }
    public string? EscalationOwner { get; set; }
    public bool Required { get; set; }
    public bool Enabled { get; set; }
    public int SortOrder { get; set; }

    public ICollection<WorkflowStepDependency> Dependencies { get; set; } = new List<WorkflowStepDependency>();
}
