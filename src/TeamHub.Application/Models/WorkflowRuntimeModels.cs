using TeamHub.Domain.Enums;

namespace TeamHub.Application.Models;

public sealed class StartWorkflowRequest
{
    public string WorkflowKey { get; set; } = string.Empty;
    public string InstanceName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string StartedBy { get; set; } = "admin@local";
    public string? MetadataJson { get; set; }
}

public sealed class StepCompletionRequest
{
    public Guid StepInstanceId { get; set; }
    public string Actor { get; set; } = string.Empty;
    public bool IsAdminOverride { get; set; }
    public string? Comment { get; set; }
}

public sealed class WorkflowSummaryDto
{
    public Guid Id { get; set; }
    public string InstanceName { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    public int Version { get; set; }
    public WorkflowInstanceStatus Status { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? NextDueAtUtc { get; set; }
    public int ProgressPercent { get; set; }
    public int OverdueCount { get; set; }
    public string ActiveOwners { get; set; } = string.Empty;
    public string ActiveSteps { get; set; } = string.Empty;
}

public sealed class DashboardSummaryDto
{
    public int ActiveWorkflows { get; set; }
    public int OverdueSteps { get; set; }
    public int CompletedThisWeek { get; set; }
    public int ActiveSteps { get; set; }
}

public sealed class TaskItemDto
{
    public Guid StepInstanceId { get; set; }
    public Guid WorkflowInstanceId { get; set; }
    public string InstanceName { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    public string StepName { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public DateTime? DueAtUtc { get; set; }
    public bool IsOverdue { get; set; }
}
