using System.Text.Json.Serialization;

namespace TeamHub.FlowDesigner.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FlowMode
{
    DiagramOnly,
    ExecutableWorkflow
}
