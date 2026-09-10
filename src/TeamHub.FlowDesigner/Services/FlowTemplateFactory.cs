using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Services;

internal static class FlowTemplateFactory
{
    public static void Apply(FlowDefinition flow, FlowTemplate template)
    {
        switch (template)
        {
            case FlowTemplate.Blank:
                return;
            case FlowTemplate.IntegrationQa:
                ApplyIntegrationQa(flow);
                return;
            case FlowTemplate.Onboarding:
                ApplyOnboarding(flow);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(template), template, "Unknown flow template.");
        }
    }

    private static void ApplyOnboarding(FlowDefinition flow)
    {
        flow.DiagramType = DiagramType.WorkCenterWorkflow;
        flow.Description = "Reusable onboarding workflow with PIC approval, parallel security briefing, and dependent lab access.";
        flow.Metadata["workflowKey"] = "ONBOARDING";
        flow.Metadata["enabled"] = "true";
        flow.Nodes =
        [
            StyledNode("onboarding-start", NodeType.Start, "Start onboarding", 460, 40, 190, 76, "green", "", "vertical"),
            WorkTask("onboarding-eoo", "Create an EOO ticket", 430, 170, "CREATE_EOO_TICKET", 24, "blue"),
            StyledNode("onboarding-split", NodeType.ParallelGateway, "Begin parallel onboarding work", 490, 310, 100, 100, "purple", "", "vertical"),
            WorkTask("onboarding-approval", "Approval from PIC UAT", 245, 470, "PIC_UAT_APPROVAL", 48, "orange"),
            WorkTask("onboarding-security", "Security Briefing", 675, 470, "SECURITY_BRIEFING", 24, "green"),
            WorkTask("onboarding-lab", "Lab Access", 245, 640, "LAB_ACCESS", 24, "blue"),
            StyledNode("onboarding-join", NodeType.ParallelGateway, "Complete parallel onboarding work", 490, 790, 100, 100, "purple", "", "vertical"),
            StyledNode("onboarding-end", NodeType.End, "Onboarding complete", 460, 950, 190, 76, "green", "", "vertical")
        ];
        flow.Connections =
        [
            Edge("onboarding-edge-1", "onboarding-start", "onboarding-eoo"),
            Edge("onboarding-edge-2", "onboarding-eoo", "onboarding-split"),
            Edge("onboarding-edge-3", "onboarding-split", "onboarding-approval", sourcePort: "output_1"),
            Edge("onboarding-edge-4", "onboarding-split", "onboarding-security", sourcePort: "output_2"),
            Edge("onboarding-edge-5", "onboarding-approval", "onboarding-lab"),
            Edge("onboarding-edge-6", "onboarding-lab", "onboarding-join", targetPort: "input_1"),
            Edge("onboarding-edge-7", "onboarding-security", "onboarding-join", targetPort: "input_2"),
            Edge("onboarding-edge-8", "onboarding-join", "onboarding-end")
        ];
    }

    private static void ApplyIntegrationQa(FlowDefinition flow)
    {
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

    private static FlowNode Node(
        string id,
        NodeType type,
        string title,
        double x,
        double y,
        NodeComment? comment = null,
        string description = "",
        double? width = null,
        double? height = null) => new()
    {
        Id = id,
        Type = type,
        Title = title,
        Description = description,
        X = x,
        Y = y,
        Width = width,
        Height = height,
        Comments = comment is null ? [] : [comment]
    };

    private static FlowNode StyledNode(
        string id,
        NodeType type,
        string title,
        double x,
        double y,
        double width,
        double height,
        string tone,
        string presentationStyle,
        string portLayout,
        string description = "")
    {
        var node = Node(id, type, title, x, y, description: description, width: width, height: height);
        ApplyStyle(node, tone, presentationStyle, portLayout);
        return node;
    }

    private static void ApplyStyle(FlowNode node, string tone, string presentationStyle, string portLayout)
    {
        node.CustomProperties["tone"] = tone;
        node.CustomProperties["presentationStyle"] = presentationStyle;
        node.CustomProperties["portLayout"] = portLayout;
    }

    private static FlowNode WorkTask(
        string id,
        string title,
        double x,
        double y,
        string stepKey,
        double expectedDurationHours,
        string tone)
    {
        var node = StyledNode(id, NodeType.Activity, title, x, y, 220, 86, tone, "step", "vertical");
        node.CustomProperties["stepKey"] = stepKey;
        node.CustomProperties["owner"] = "";
        node.CustomProperties["ownerType"] = "User";
        node.CustomProperties["expectedDurationHours"] = expectedDurationHours.ToString(System.Globalization.CultureInfo.InvariantCulture);
        node.CustomProperties["required"] = "true";
        node.CustomProperties["enabled"] = "true";
        return node;
    }

    private static FlowConnection Edge(
        string id,
        string source,
        string target,
        string label = "",
        string sourcePort = "output_1",
        string targetPort = "input_1")
    {
        var tone = id.StartsWith("direct-", StringComparison.Ordinal) || target.StartsWith("direct-", StringComparison.Ordinal) ? "red"
            : id.StartsWith("proactive-", StringComparison.Ordinal) || target.StartsWith("proactive-", StringComparison.Ordinal) ? "green"
            : id.StartsWith("learn-", StringComparison.Ordinal) ? "purple"
            : id is "milestone-edge-6" or "milestone-edge-8" or "health-edge-5" ? "green"
            : id is "milestone-edge-7" or "milestone-edge-9" || id.StartsWith("health-edge-6", StringComparison.Ordinal) || id.StartsWith("health-edge-7", StringComparison.Ordinal) || id.StartsWith("health-edge-8", StringComparison.Ordinal) || id.StartsWith("health-edge-9", StringComparison.Ordinal) ? "red"
            : id.StartsWith("milestone-", StringComparison.Ordinal) || target.StartsWith("milestone-", StringComparison.Ordinal) ? "blue"
            : id.StartsWith("health-", StringComparison.Ordinal) || target.StartsWith("health-", StringComparison.Ordinal) ? "orange"
            : "";

        return new FlowConnection
        {
            Id = id,
            SourceNodeId = source,
            TargetNodeId = target,
            SourcePort = sourcePort,
            TargetPort = targetPort,
            Label = label,
            Metadata = string.IsNullOrEmpty(tone)
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tone"] = tone }
        };
    }
}
