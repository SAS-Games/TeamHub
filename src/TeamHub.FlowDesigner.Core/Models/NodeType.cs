using System.Text.Json.Serialization;

namespace TeamHub.FlowDesigner.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NodeType
{
    Start,
    Task,
    Decision,
    Approval,
    Parallel,
    Wait,
    Notification,
    AutomatedAction,
    End,
    Custom
}
