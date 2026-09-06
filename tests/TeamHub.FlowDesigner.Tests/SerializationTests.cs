using TeamHub.FlowDesigner.Core.Models;
using TeamHub.FlowDesigner.Serialization;

namespace TeamHub.FlowDesigner.Tests;

public sealed class SerializationTests
{
    [Fact]
    public void RoundTrip_PreservesGenericGraph()
    {
        var flow = new FlowDefinition
        {
            Name = "Release plan",
            Nodes = [new FlowNode { Id = "start", Type = NodeType.Start, X = 123.5, Metadata = { ["owner"] = "Sam" } }],
            Connections = []
        };
        var serializer = new SystemTextJsonFlowSerializer();

        var restored = serializer.Deserialize(serializer.Serialize(flow));

        Assert.Equal(flow.Id, restored.Id);
        Assert.Equal(NodeType.Start, restored.Nodes.Single().Type);
        Assert.Equal(123.5, restored.Nodes.Single().X);
        Assert.Equal("Sam", restored.Nodes.Single().Metadata["owner"]);
    }
}
