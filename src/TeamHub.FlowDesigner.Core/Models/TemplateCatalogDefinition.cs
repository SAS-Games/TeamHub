namespace TeamHub.FlowDesigner.Core.Models;

public static class TemplateKinds
{
    public const string FlowDiagram = "FlowDiagram";
    public const string FlowDiagramBundle = "FlowDiagramBundle";
    public const string WorkflowDefinition = "WorkflowDefinition";
}

public sealed class FlowDiagramTemplateBundle
{
    public Guid RootFlowId { get; set; }
    public List<FlowDefinition> Flows { get; set; } = [];
}

public sealed class TemplateCatalogDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TemplateKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public string TemplateKind { get; set; } = TemplateKinds.FlowDiagram;
    public DiagramType? DiagramType { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public bool IsBuiltIn { get; set; }
    public bool AdminOnly { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record TemplateCatalogSummary(
    Guid Id,
    string TemplateKey,
    string Name,
    string Description,
    string Category,
    string TemplateKind,
    DiagramType? DiagramType,
    int Version,
    bool IsBuiltIn,
    bool AdminOnly,
    DateTimeOffset UpdatedAt);

public sealed record SaveFlowTemplateRequest(
    string TemplateKey,
    string Name,
    string Description,
    string Category);
