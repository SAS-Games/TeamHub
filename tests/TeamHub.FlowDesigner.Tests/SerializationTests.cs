using TeamHub.FlowDesigner.Core.Models;
using TeamHub.FlowDesigner.Serialization;

namespace TeamHub.FlowDesigner.Tests;

public sealed class SerializationTests
{
    [Fact]
    public void RoundTrip_PreservesGenericGraph()
    {
        var childFlowId = Guid.NewGuid();
        var flow = new FlowDefinition
        {
            Name = "Release plan",
            DiagramType = DiagramType.CodeFlow,
            Nodes =
            [
                new FlowNode { Id = "start", Type = NodeType.Start, X = 123.5, ChildFlowId = childFlowId, CustomProperties = { ["notes"] = "Entry point" }, Comments = [new NodeComment { Author = "Sam", Body = "Looks good" }] },
                new FlowNode { Id = "router", Type = NodeType.Process, CustomProperties = { ["inputPins"] = "3", ["outputPins"] = "4", ["portLayout"] = "left-bottom" } }
            ],
            Connections = []
        };
        var serializer = new SystemTextJsonFlowSerializer();

        var restored = serializer.Deserialize(serializer.Serialize(flow));
        var restoredStart = restored.Nodes.Single(node => node.Id == "start");
        var restoredRouter = restored.Nodes.Single(node => node.Id == "router");

        Assert.Equal(flow.Id, restored.Id);
        Assert.Equal(NodeType.Start, restoredStart.Type);
        Assert.Equal(DiagramType.CodeFlow, restored.DiagramType);
        Assert.Equal(123.5, restoredStart.X);
        Assert.Equal(childFlowId, restoredStart.ChildFlowId);
        Assert.Equal("Entry point", restoredStart.CustomProperties["notes"]);
        Assert.Equal("3", restoredRouter.CustomProperties["inputPins"]);
        Assert.Equal("4", restoredRouter.CustomProperties["outputPins"]);
        Assert.Equal("left-bottom", restoredRouter.CustomProperties["portLayout"]);
        Assert.Equal("Looks good", restoredStart.Comments.Single().Body);
    }

    [Fact]
    public void Deserialize_UpgradesEarlierWorkflowSpecificSymbols()
    {
        var serializer = new SystemTextJsonFlowSerializer();
        var flow = new FlowDefinition
        {
            DiagramType = DiagramType.StandardFlowchart,
            Nodes = [new FlowNode { Id = "step", Type = NodeType.Process, Title = "Review" }]
        };
        var earlierJson = serializer.Serialize(flow)
            .Replace("\"diagramType\":\"StandardFlowchart\"", "\"mode\":\"DiagramOnly\"")
            .Replace("\"type\":\"Process\"", "\"type\":\"Approval\"");

        var restored = serializer.Deserialize(earlierJson);

        Assert.Equal(DiagramType.StandardFlowchart, restored.DiagramType);
        Assert.Equal(NodeType.Activity, restored.Nodes.Single().Type);
    }

    [Fact]
    public void Deserialize_UpgradesRemovedNoteNodeToProcess()
    {
        var serializer = new SystemTextJsonFlowSerializer();
        const string earlierJson = "{\"name\":\"Legacy notes\",\"diagramType\":\"StandardFlowchart\",\"nodes\":[{\"id\":\"note-1\",\"type\":\"Note\",\"title\":\"Context\"}],\"connections\":[]}";

        var restored = serializer.Deserialize(earlierJson);

        Assert.Equal(NodeType.Process, restored.Nodes.Single().Type);
        Assert.Equal("Context", restored.Nodes.Single().Title);
    }
}
