using System.Text.Json.Serialization;

namespace TeamHub.FlowDesigner.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagramType
{
    StandardFlowchart,
    BusinessWorkflow,
    CodeFlow
}

public static class DiagramTypeExtensions
{
    public static string ToDisplayName(this DiagramType diagramType) => diagramType switch
    {
        DiagramType.StandardFlowchart => "Standard flowchart",
        DiagramType.BusinessWorkflow => "Business workflow",
        DiagramType.CodeFlow => "Code flow",
        _ => "Flow diagram"
    };
}
