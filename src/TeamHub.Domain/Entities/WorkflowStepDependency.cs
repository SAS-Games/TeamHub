namespace TeamHub.Domain.Entities;

public sealed class WorkflowStepDependency
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkflowStepDefinitionId { get; set; }
    public WorkflowStepDefinition WorkflowStepDefinition { get; set; } = null!;

    public Guid DependsOnStepDefinitionId { get; set; }
}
