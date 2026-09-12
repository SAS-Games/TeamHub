namespace TeamHub.FlowDesigner.Persistence;

internal sealed class PublishedFlowEntity
{
    public Guid Id { get; set; }
    public Guid SourceFlowId { get; set; }
    public Guid PublicationRequestId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DiagramType { get; set; } = string.Empty;
    public int NodeCount { get; set; }
    public int SourceVersion { get; set; }
    public int PublicationVersion { get; set; }
    public string GraphJson { get; set; } = string.Empty;
    public string SnapshotHash { get; set; } = string.Empty;
    public DateTime PublishedAt { get; set; }
    public string PublishedBy { get; set; } = string.Empty;
    public bool IsCurrent { get; set; }
}
