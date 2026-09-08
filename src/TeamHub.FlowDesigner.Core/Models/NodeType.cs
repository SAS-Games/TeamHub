using System.Text.Json.Serialization;

namespace TeamHub.FlowDesigner.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NodeType
{
    Start,
    End,
    Process,
    Decision,
    InputOutput,
    Document,
    DataStore,
    Subprocess,
    Connector,
    ManualInput,
    Preparation,
    Activity,
    Event,
    Gateway,
    ParallelGateway,
    Note
}
