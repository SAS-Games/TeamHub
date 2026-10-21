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
    public string? ReminderSubjectTemplate { get; set; }
    public string? ReminderBodyTemplate { get; set; }
    public string? OverdueSubjectTemplate { get; set; }
    public string? OverdueBodyTemplate { get; set; }
    public string? EscalationSubjectTemplate { get; set; }
    public string? EscalationBodyTemplate { get; set; }
    public double? EscalationAfterHours { get; set; }
    public string? EscalationOwner { get; set; }
    public double? EscalationLevel2AfterHours { get; set; }
    public string? EscalationLevel2Owners { get; set; }
    public double? EscalationLevel3AfterHours { get; set; }
    public string? EscalationLevel3Owners { get; set; }
    public bool Required { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
}
