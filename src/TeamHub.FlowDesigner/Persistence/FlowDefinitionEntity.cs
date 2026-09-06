namespace TeamHub.FlowDesigner.Persistence;

internal sealed class FlowDefinitionEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string GraphJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public int Version { get; set; }
    public int NodeCount { get; set; }
}
