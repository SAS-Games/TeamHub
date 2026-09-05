namespace TeamHub.Domain.Entities;

public sealed class WorkflowDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string WorkflowKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Version { get; set; }
    public string ConfigHash { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool Enabled { get; set; }
    public DateTime ImportedAtUtc { get; set; }

    public ICollection<WorkflowStepDefinition> Steps { get; set; } = new List<WorkflowStepDefinition>();
}
