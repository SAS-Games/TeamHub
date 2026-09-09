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
    public void PresentationNodes_DoNotRequireConnections()
    {
        var flow = ConnectedFlow();
        flow.Nodes.Add(new FlowNode { Id = "section", Type = NodeType.Section, Title = "Support activities" });
        flow.Nodes.Add(new FlowNode { Id = "heading", Type = NodeType.Annotation, Title = "Studio support workflow" });

        var result = _validator.Validate(flow);

        Assert.DoesNotContain(result.Issues, issue => issue.Code == "orphan-node" &&
            (issue.ElementId == "section" || issue.ElementId == "heading"));
    }

    [Fact]
    public void BusinessWorkflow_DoesNotRequireFormalStartOrEndSymbols()
    {
        var flow = new FlowDefinition
        {
            Name = "Operating model",
            DiagramType = DiagramType.BusinessWorkflow,
            Nodes =
            [
                new FlowNode { Id = "activity-1", Type = NodeType.Activity, Title = "Receive request" },
                new FlowNode { Id = "activity-2", Type = NodeType.Activity, Title = "Resolve request" }
            ],
            Connections =
            [
                new FlowConnection { Id = "edge", SourceNodeId = "activity-1", TargetNodeId = "activity-2", SourcePort = "output_1", TargetPort = "input_1" }
            ]
        };

        var result = _validator.Validate(flow);

        Assert.DoesNotContain(result.Issues, issue => issue.Code is "missing-start" or "start-count" or "missing-end");
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
