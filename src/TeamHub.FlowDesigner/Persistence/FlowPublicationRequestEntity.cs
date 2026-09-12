namespace TeamHub.FlowDesigner.Persistence;

internal sealed class FlowPublicationRequestEntity
{
    public Guid Id { get; set; }
    public Guid FlowId { get; set; }
    public string FlowName { get; set; } = string.Empty;
    public string DiagramType { get; set; } = string.Empty;
    public int SourceVersion { get; set; }
    public int NodeCount { get; set; }
    public string SnapshotJson { get; set; } = string.Empty;
    public string SnapshotHash { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
}
