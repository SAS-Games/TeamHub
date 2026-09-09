using System.Text.Json.Serialization;

namespace TeamHub.FlowDesigner.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagramType
{
    StandardFlowchart,
    BusinessWorkflow,
    CodeFlow,
    WorkCenterWorkflow
}

public static class DiagramTypeExtensions
{
    public static string ToDisplayName(this DiagramType diagramType) => diagramType switch
    {
        DiagramType.StandardFlowchart => "Standard flowchart",
        DiagramType.BusinessWorkflow => "Business workflow",
        DiagramType.CodeFlow => "Code flow",
        DiagramType.WorkCenterWorkflow => "Work Center workflow",
        _ => "Flow diagram"
    };
}
