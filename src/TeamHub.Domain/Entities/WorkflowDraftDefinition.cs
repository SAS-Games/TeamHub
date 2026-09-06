namespace TeamHub.Domain.Entities;

public sealed class WorkflowDraftDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string WorkflowKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? PublishedAtUtc { get; set; }

    public ICollection<WorkflowDraftStepDefinition> Steps { get; set; } = new List<WorkflowDraftStepDefinition>();
}