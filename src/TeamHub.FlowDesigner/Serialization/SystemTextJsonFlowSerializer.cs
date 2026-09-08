using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Serialization;

public sealed class SystemTextJsonFlowSerializer : IFlowSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Serialize(FlowDefinition flow) => JsonSerializer.Serialize(flow, Options);

    public FlowDefinition Deserialize(string json)
    {
        var document = JsonNode.Parse(json)?.AsObject()
            ?? throw new JsonException("The flow document was empty or invalid.");

        var storedDiagramType = document["diagramType"]?.GetValue<string>()
            ?? document["mode"]?.GetValue<string>();
        document["diagramType"] = storedDiagramType switch
        {
            "DiagramOnly" => nameof(DiagramType.StandardFlowchart),
            "ExecutableWorkflow" => nameof(DiagramType.BusinessWorkflow),
            null => nameof(DiagramType.StandardFlowchart),
            var value => value
        };
        document.Remove("mode");

        if (document["nodes"] is JsonArray nodes)
        {
            foreach (var node in nodes.OfType<JsonObject>())
            {
                node["type"] = node["type"]?.GetValue<string>() switch
                {
                    "Task" or "Wait" or "AutomatedAction" or "Custom" or "Note" => nameof(NodeType.Process),
                    "Approval" => nameof(NodeType.Activity),
                    "Parallel" => nameof(NodeType.ParallelGateway),
                    "Notification" => nameof(NodeType.Document),
                    var value => value
                };
            }
        }

        return document.Deserialize<FlowDefinition>(Options)
            ?? throw new JsonException("The flow document was empty or invalid.");
    }
}
