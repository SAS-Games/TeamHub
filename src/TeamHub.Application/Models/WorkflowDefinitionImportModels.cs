namespace TeamHub.Application.Models;

public sealed class WorkflowDefinitionDto
{
    public string WorkflowKey { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public List<WorkflowStepDefinitionDto> Steps { get; set; } = new();
}

public sealed class WorkflowStepDefinitionDto
{
    public string StepKey { get; set; } = string.Empty;
    public string StepName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string OwnerType { get; set; } = "Email";
    public string Owner { get; set; } = string.Empty;
    public double ExpectedDurationHours { get; set; }
    public List<string> DependsOn { get; set; } = new();
    public double? ReminderAfterHours { get; set; }
    public double? ReminderRepeatHours { get; set; }
    public double? EscalationAfterHours { get; set; }
    public string? EscalationOwner { get; set; }
    public bool Required { get; set; }
    public bool Enabled { get; set; }
    public int SortOrder { get; set; }
}

public sealed class ConfigurationSyncResult
{
    public bool Success { get; set; }
    public DateTime SyncedAtUtc { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> ImportedWorkflowSummaries { get; set; } = new();
}
