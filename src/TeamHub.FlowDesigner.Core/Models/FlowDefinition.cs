namespace TeamHub.FlowDesigner.Core.Models;

public sealed class FlowDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Untitled flow";
    public string Description { get; set; } = string.Empty;
    public DiagramType DiagramType { get; set; } = DiagramType.StandardFlowchart;
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FlowNode> Nodes { get; set; } = [];
    public List<FlowConnection> Connections { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedBy { get; set; }
    public int Version { get; set; } = 1;
}
