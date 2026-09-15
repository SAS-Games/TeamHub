namespace TeamHub.FlowDesigner.DependencyInjection;

public sealed class FlowDesignerOptions
{
    public string ConnectionString { get; set; } = "Data Source=data/flowdesigner.db";
    public string LibraryConnectionString { get; set; } = "Data Source=data/flow-library.db";
    public string? LegacyTemplateConnectionString { get; set; }
    public string? LegacyPublishedConnectionString { get; set; }
}
