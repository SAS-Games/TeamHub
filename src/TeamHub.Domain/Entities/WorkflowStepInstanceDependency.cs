namespace TeamHub.Domain.Entities;

public sealed class WorkflowStepInstanceDependency
{
    public Guid WorkflowStepInstanceId { get; set; }
    public WorkflowStepInstance WorkflowStepInstance { get; set; } = null!;

    public Guid DependsOnWorkflowStepInstanceId { get; set; }
}
