namespace TeamHub.FlowDesigner.DependencyInjection;

public sealed class FlowDesignerOptions
{
    public string ConnectionString { get; set; } = "Data Source=data/flowdesigner.db";
    public string TemplateConnectionString { get; set; } = "Data Source=data/templates.db";
    public string? PublishedConnectionString { get; set; }
}
