using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Services;

internal static class FlowTemplateFactory
{
    public static void Apply(FlowDefinition flow, FlowTemplate template)
    {
        if (template == FlowTemplate.Blank)
        {
            return;
        }

        if (template != FlowTemplate.IntegrationQa)
        {
            throw new ArgumentOutOfRangeException(nameof(template), template, "Unknown flow template.");
        }

        flow.DiagramType = DiagramType.StandardFlowchart;
        flow.Description = "Example integration QA lifecycle with success paths, defect loops, evidence, and exit criteria.";
        flow.Nodes =
        [
            Node("qa-start", NodeType.Start, "Build accepted", 40, 280),
            Node("qa-build", NodeType.Process, "Verify build and release notes", 260, 280),
            Node("qa-data", NodeType.InputOutput, "Load test data and configuration", 500, 280),
            Node("qa-deploy", NodeType.Process, "Deploy to integration environment", 740, 280),
            Node("qa-suite", NodeType.Subprocess, "Run smoke and integration suite", 980, 280),
            Node("qa-critical-gate", NodeType.Decision, "All critical tests pass?", 1220, 260,
                new NodeComment { Author = "Template", Body = "Attach test-run evidence before taking the Yes path." }),
            Node("qa-quality", NodeType.Process, "Run regression, security, and performance checks", 1460, 80),
            Node("qa-exit-gate", NodeType.Decision, "QA exit criteria met?", 1700, 260),
            Node("qa-report", NodeType.Document, "Publish QA report and evidence", 1940, 280),
            Node("qa-end", NodeType.End, "Release candidate ready", 2180, 280),
            Node("qa-defect", NodeType.Document, "Log defects with evidence", 1460, 500),
            Node("qa-fix", NodeType.Process, "Fix, rebuild, and update notes", 980, 500)
        ];
        flow.Connections =
        [
            Edge("qa-edge-1", "qa-start", "qa-build"),
            Edge("qa-edge-2", "qa-build", "qa-data"),
            Edge("qa-edge-3", "qa-data", "qa-deploy"),
            Edge("qa-edge-4", "qa-deploy", "qa-suite"),
            Edge("qa-edge-5", "qa-suite", "qa-critical-gate"),
            Edge("qa-edge-6", "qa-critical-gate", "qa-quality", "Yes", "output_1"),
            Edge("qa-edge-7", "qa-critical-gate", "qa-defect", "No", "output_2"),
            Edge("qa-edge-8", "qa-quality", "qa-exit-gate"),
            Edge("qa-edge-9", "qa-exit-gate", "qa-report", "Yes", "output_1"),
            Edge("qa-edge-10", "qa-exit-gate", "qa-defect", "No", "output_2"),
            Edge("qa-edge-11", "qa-defect", "qa-fix"),
            Edge("qa-edge-12", "qa-fix", "qa-deploy"),
            Edge("qa-edge-13", "qa-report", "qa-end")
        ];
    }

    private static FlowNode Node(string id, NodeType type, string title, double x, double y, NodeComment? comment = null) => new()
    {
        Id = id,
        Type = type,
        Title = title,
        X = x,
        Y = y,
        Comments = comment is null ? [] : [comment]
    };

    private static FlowConnection Edge(string id, string source, string target, string label = "", string sourcePort = "output_1") => new()
    {
        Id = id,
        SourceNodeId = source,
        TargetNodeId = target,
        SourcePort = sourcePort,
        TargetPort = "input_1",
        Label = label
    };
}
