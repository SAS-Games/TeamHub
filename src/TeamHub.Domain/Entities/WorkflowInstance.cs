using TeamHub.Domain.Enums;

namespace TeamHub.Domain.Entities;

public sealed class WorkflowInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkflowDefinitionId { get; set; }
    public WorkflowDefinition WorkflowDefinition { get; set; } = null!;

    public string InstanceName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? MetadataJson { get; set; }
    public WorkflowInstanceStatus Status { get; set; } = WorkflowInstanceStatus.InProgress;
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string StartedBy { get; set; } = string.Empty;

    public ICollection<WorkflowStepInstance> Steps { get; set; } = new List<WorkflowStepInstance>();
}
