using System.Text.Json;
using TeamHub.FlowDesigner.Core.Models;
using TeamHub.FlowDesigner.Core.Validation;
using TeamHub.FlowDesigner.Validation;

namespace TeamHub.FlowDesigner.Tests;

public sealed class ValidationTests
{
    private readonly FlowValidator _validator = new();

    [Fact]
    public void Severity_IsSerializedAsStringForBrowserClient()
    {
        var issue = new FlowValidationIssue("example", "Example", ValidationSeverity.Warning);

        var json = JsonSerializer.Serialize(issue, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"severity\":\"Warning\"", json);
    }

    [Fact]
    public void ValidConnectedFlow_HasNoIssues()
    {
        var flow = ConnectedFlow();

        var result = _validator.Validate(flow);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void DuplicateNodeIds_AreStructuralErrors()
    {
        var flow = ConnectedFlow();
        flow.Nodes.Add(new FlowNode { Id = "start", Type = NodeType.Process });

        var result = _validator.Validate(flow);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "duplicate-node-id" && issue.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void MissingConnectionTarget_IsStructuralError()
    {
        var flow = ConnectedFlow();
        flow.Connections[0].TargetNodeId = "missing";

        var result = _validator.Validate(flow);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "missing-target");
    }

    [Fact]
    public void OrphanNode_IsWarningAndDoesNotBlockSave()
    {
        var flow = ConnectedFlow();
        flow.Nodes.Add(new FlowNode { Id = "orphan", Title = "Loose task" });

        var result = _validator.Validate(flow);

        Assert.True(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "orphan-node" && issue.ElementId == "orphan");
    }

    [Fact]
    public void UnconnectedNote_IsAllowed()
    {
        var flow = ConnectedFlow();
        flow.Nodes.Add(new FlowNode { Id = "note", Type = NodeType.Note, Title = "Context" });

        var result = _validator.Validate(flow);

        Assert.DoesNotContain(result.Issues, issue => issue.Code == "orphan-node" && issue.ElementId == "note");
    }

    [Fact]
    public void DecisionWithOnePath_GetsGuidanceWarning()
    {
        var flow = ConnectedFlow();
        flow.Nodes[0].Type = NodeType.Decision;

        var result = _validator.Validate(flow);

        Assert.Contains(result.Issues, issue => issue.Code == "incomplete-branch" && issue.ElementId == "start");
    }

    internal static FlowDefinition ConnectedFlow() => new()
    {
        Name = "Connected",
        Nodes =
        [
            new FlowNode { Id = "start", Type = NodeType.Start, Title = "Start" },
            new FlowNode { Id = "end", Type = NodeType.End, Title = "End" }
        ],
        Connections = [new FlowConnection { Id = "edge", SourceNodeId = "start", TargetNodeId = "end", SourcePort = "output_1", TargetPort = "input_1" }]
    };
}
