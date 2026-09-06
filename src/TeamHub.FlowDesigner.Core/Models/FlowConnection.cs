namespace TeamHub.FlowDesigner.Core.Models;

public sealed class FlowConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceNodeId { get; set; } = string.Empty;
    public string TargetNodeId { get; set; } = string.Empty;
    public string? SourcePort { get; set; }
    public string? TargetPort { get; set; }
    public string? Label { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
