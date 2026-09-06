namespace TeamHub.Domain.Entities;

public sealed class WorkflowDraftStepDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkflowDraftDefinitionId { get; set; }
    public WorkflowDraftDefinition WorkflowDraftDefinition { get; set; } = null!;

    public string StepKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string OwnerType { get; set; } = "Email";
    public string Owner { get; set; } = string.Empty;
    public double ExpectedDurationHours { get; set; }
    public string DependsOnCsv { get; set; } = string.Empty;
    public double? ReminderAfterHours { get; set; }
    public double? ReminderRepeatHours { get; set; }
    public double? EscalationAfterHours { get; set; }
    public string? EscalationOwner { get; set; }
    public bool Required { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
}