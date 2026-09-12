namespace TeamHub.FlowDesigner.Core.Models;

public sealed class FlowNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public NodeType Type { get; set; } = NodeType.Process;
    public string Title { get; set; } = "Process";
    public string Description { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public Guid? ChildFlowId { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CustomProperties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<NodeComment> Comments { get; set; } = [];
}
