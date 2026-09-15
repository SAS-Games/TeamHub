namespace TeamHub.FlowDesigner.Persistence;

internal sealed class DeletedTemplateDefinitionEntity
{
    public string TemplateKey { get; set; } = string.Empty;
    public DateTime DeletedAt { get; set; }
}
