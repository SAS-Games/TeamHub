using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Core.Contracts;

public interface IFlowSerializer
{
    string Serialize(FlowDefinition flow);
    FlowDefinition Deserialize(string json);
    string SerializeBundle(FlowDiagramTemplateBundle bundle);
    FlowDiagramTemplateBundle DeserializeBundle(string json);
}
