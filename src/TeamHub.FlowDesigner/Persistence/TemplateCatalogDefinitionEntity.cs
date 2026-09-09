namespace TeamHub.FlowDesigner.Persistence;

internal sealed class TemplateCatalogDefinitionEntity
{
    public Guid Id { get; set; }
    public string TemplateKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string TemplateKind { get; set; } = string.Empty;
    public string? DiagramType { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public int Version { get; set; }
    public bool IsActive { get; set; }
    public bool IsBuiltIn { get; set; }
    public bool AdminOnly { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
