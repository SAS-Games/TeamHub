using System.Text.Json;
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

    public FlowDefinition Deserialize(string json) =>
        JsonSerializer.Deserialize<FlowDefinition>(json, Options)
        ?? throw new JsonException("The flow document was empty or invalid.");
}
