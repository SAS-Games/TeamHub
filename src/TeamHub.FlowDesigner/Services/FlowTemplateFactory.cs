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
            case FlowTemplate.StudioSupport:
                ApplyStudioSupport(flow);
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

    private static void ApplyStudioSupport(FlowDefinition flow)
    {
        flow.DiagramType = DiagramType.BusinessWorkflow;
        flow.Description = "XYZ Team studio support operating model with four support tracks, project-health decisions, and a continuous learning cycle.";
        flow.Nodes =
        [
            PresentationNode("support-title", NodeType.Annotation, "XYZ Team – Studio Support Workflow", "One-to-One Support  |  Proactive Partnership  |  Better Games Together", 20, 20, 1440, 86, "blue", "banner"),
            PresentationNode("support-studios", NodeType.Annotation, "▥  Our Studios (Game Teams)", "Studio A        Development Teams\nStudio B        Production Teams\nStudio C        External Studios\nStudio D        Game Teams\n…", 20, 128, 340, 190, "blue", "card"),
            PresentationNode("support-pic", NodeType.Annotation, "One-to-One\nPIC Model", "Primary interface", 390, 166, 190, 112, "blue", "plain"),
            PresentationNode("support-team", NodeType.Annotation, "●●●  XYZ Team", "Dedicated Studio PICs\n\n• Understand studio needs\n• Provide support (reactive & proactive)\n• Review milestones\n• Monitor project health\n• Build long-term partnership", 610, 128, 370, 190, "green", "card"),
            PresentationNode("support-guidance", NodeType.Annotation, "← Guidance / Skill-up", "", 980, 172, 110, 80, "purple", "plain"),
            PresentationNode("support-experts", NodeType.Annotation, "●●  Expert / Specialist Team", "Behind the Scenes\n\n• Mentoring & guidance\n• Rendering, debugging, profiling\n• System development\n• Specialized technical areas\n• Help build XYZ Team capability", 1090, 128, 370, 190, "purple", "card"),
            StyledNode("support-core", NodeType.Activity, "Core Support Activities", 500, 346, 480, 62, "blue", "banner", "vertical"),

            PresentationNode("direct-frame", NodeType.Section, "A. Direct Studio Support", "Reactive Support", 20, 440, 330, 1050, "red", "band"),
            PresentationNode("proactive-frame", NodeType.Section, "B. Proactive Support", "Preventive / Future-focused", 370, 440, 330, 1050, "green", "band"),
            PresentationNode("milestone-frame", NodeType.Section, "C. Milestone Build Review", "Independent Assessment", 720, 440, 330, 1050, "blue", "band"),
            PresentationNode("health-frame", NodeType.Section, "D. Project Health & Risk Management", "Overall Project Health", 1070, 440, 390, 1050, "orange", "band"),

            Node("direct-1", NodeType.Activity, "1. Studio Raises Request", 45, 530, description: "Technical / Development / Others", width: 280, height: 76),
            Node("direct-2", NodeType.Activity, "2. XYZ PIC", 45, 650, description: "Understand & triage", width: 280, height: 76),
            Node("direct-3", NodeType.Activity, "3. Investigation / Analysis", 45, 770, description: "With team collaboration if needed", width: 280, height: 82),
            Node("direct-4", NodeType.Activity, "4. Solution / Recommendation", 45, 895, width: 280, height: 70),
            Node("direct-5", NodeType.Activity, "5. Share with Studio", 45, 1010, width: 280, height: 70),
            Node("direct-6", NodeType.Activity, "6. Follow-up / Closure", 45, 1125, width: 280, height: 70),
            PresentationNode("direct-examples", NodeType.Annotation, "Examples", "• Debugging\n• Technical consultation\n• Implementation guidance\n• Performance issues\n• System-related problems\n• Development blockers\n• Other project-specific support", 45, 1240, 280, 210, "red", "card"),

            Node("proactive-1", NodeType.Activity, "1. Continuous Studio Understanding", 395, 520, description: "Project status, roadmap, challenges", width: 280, height: 76),
            Node("proactive-2", NodeType.Activity, "2. Identify Potential Needs / Gaps", 395, 625, width: 280, height: 70),
            Node("proactive-3", NodeType.Activity, "3. Proactive Investigation / Analysis", 395, 725, description: "Profiling  •  Code review\nTechnical analysis  •  Future needs", width: 280, height: 100),
            Node("proactive-4", NodeType.Activity, "4. Identify Support Opportunity", 395, 855, width: 280, height: 70),
            Node("proactive-5", NodeType.Activity, "5. Develop / Prepare Support", 395, 955, description: "Tools  •  Samples\nBest practices  •  Guidance", width: 280, height: 96),
            Node("proactive-6", NodeType.Activity, "6. Share with Studio", 395, 1080, width: 280, height: 68),
            Node("proactive-7", NodeType.Activity, "7. Improved Studio Readiness", 395, 1175, width: 280, height: 68),
            PresentationNode("proactive-examples", NodeType.Annotation, "Examples", "• Profiling and analysis\n• Code reviews\n• Tool / sample development\n• Best practices\n• Preparation for upcoming technical needs", 395, 1270, 280, 180, "green", "card"),

            Node("milestone-1", NodeType.Activity, "1. Studio Submits Milestone Build", 745, 520, width: 280, height: 66),
            Node("milestone-2", NodeType.Activity, "2. XYZ Team Review", 745, 610, description: "Technical + Player Experience", width: 280, height: 72),
            Node("milestone-3", NodeType.Activity, "3. Assess Against Milestone Criteria", 745, 705, description: "• Required deliverables\n• Expected features/content\n• Overall build quality\n• Run to play / player experience\n• Major issues or concerns", width: 280, height: 126),
            Node("milestone-4", NodeType.Document, "4. Prepare Review Report", 745, 850, width: 280, height: 68),
            Node("milestone-5", NodeType.Activity, "5. Share Report with Internal Producer & Relevant Stakeholders", 745, 940, width: 280, height: 76),
            Node("milestone-gate", NodeType.Gateway, "Build meets milestone expectations?", 787, 1035),
            Node("milestone-yes", NodeType.Activity, "No internal alignment needed", 730, 1170, width: 140, height: 88),
            Node("milestone-no", NodeType.Activity, "Internal alignment meeting with Producer & Stakeholders", 900, 1170, width: 140, height: 88),
            Node("milestone-6", NodeType.Activity, "6. Milestone Review Meeting with Studio", 745, 1280, width: 280, height: 66),
            Node("milestone-7", NodeType.Activity, "7. Share Feedback / Findings", 745, 1360, width: 280, height: 58),
            Node("milestone-8", NodeType.Activity, "8. Follow-up Actions", 745, 1430, width: 280, height: 50),

            Node("health-1", NodeType.Activity, "1. Continuous Engagement", 1105, 520, description: "Throughout development", width: 320, height: 70),
            Node("health-2", NodeType.Activity, "2. Gather Inputs From", 1105, 615, description: "Direct Support Findings  •  Proactive Support Analysis\nMilestone Reviews  •  Ongoing Studio Engagement", width: 320, height: 112),
            Node("health-3", NodeType.Activity, "3. Assess Project Health", 1105, 750, description: "• Milestone readiness\n• Production progress\n• Technical capability\n• Resource / capability gaps\n• Dependencies / blockers\n• Previous findings / actions", width: 320, height: 146),
            Node("health-4", NodeType.Activity, "4. Overall Risk Assessment", 1105, 920, width: 320, height: 62),
            Node("health-gate", NodeType.Gateway, "Studio on track?", 1167, 1005),
            Node("health-yes", NodeType.Activity, "Continue support & monitoring", 1085, 1145, width: 155, height: 82),
            Node("health-no", NodeType.Activity, "Identify & document risks", 1260, 1145, width: 165, height: 70),
            Node("health-align", NodeType.Activity, "Internal alignment", 1260, 1240, width: 165, height: 62),
            Node("health-visibility", NodeType.Activity, "Stakeholder / Management visibility", 1260, 1325, width: 165, height: 68),
            Node("health-track", NodeType.Activity, "Track mitigation & follow-up", 1260, 1415, width: 165, height: 60),

            PresentationNode("improvement-frame", NodeType.Section, "⚙  Continuous Improvement & Team Capability", "Continuous learning cycle", 20, 1530, 1440, 240, "purple", "band"),
            Node("learn-1", NodeType.Activity, "Learnings from all activities", 45, 1620, width: 145, height: 82),
            Node("learn-2", NodeType.Activity, "Identify common gaps / patterns", 220, 1620, width: 145, height: 82),
            Node("learn-3", NodeType.Activity, "Skill-up", 395, 1620, description: "Internal development", width: 145, height: 82),
            Node("learn-4", NodeType.Activity, "Tools / Samples", 570, 1620, width: 145, height: 82),
            Node("learn-5", NodeType.Activity, "Best Practices", 745, 1620, width: 145, height: 82),
            Node("learn-6", NodeType.Activity, "Stronger XYZ Team Capability", 920, 1620, width: 145, height: 82),
            Node("learn-7", NodeType.Activity, "Apply across all studios", 1095, 1620, width: 145, height: 82),
            Node("learn-8", NodeType.Activity, "Better Studio Support", 1270, 1620, width: 145, height: 82),

            PresentationNode("support-footer", NodeType.Annotation, "●●●  Expert / Specialist Team (Behind the Scenes)", "Provides mentoring and specialized guidance to help build XYZ Team capability in rendering, debugging, profiling, system development and other technical domains.\nXYZ Team owns the studio relationship and delivers the support.", 20, 1800, 1210, 112, "purple", "card"),
            PresentationNode("support-outcome", NodeType.Annotation, "Stronger Team\nGreater Support\nBetter Games", "", 1250, 1800, 210, 112, "purple", "banner")
        ];
        foreach (var node in flow.Nodes.Where(node => node.Type is not NodeType.Section and not NodeType.Annotation))
        {
            if (node.Id.StartsWith("direct-", StringComparison.Ordinal))
            {
                ApplyStyle(node, "red", "step", "vertical");
            }
            else if (node.Id.StartsWith("proactive-", StringComparison.Ordinal))
            {
                ApplyStyle(node, "green", "step", "vertical");
            }
            else if (node.Id.StartsWith("milestone-", StringComparison.Ordinal))
            {
                ApplyStyle(node, node.Id == "milestone-yes" ? "green" : node.Id == "milestone-no" ? "red" : "blue", node.Type == NodeType.Gateway ? "" : "step", "vertical");
            }
            else if (node.Id.StartsWith("health-", StringComparison.Ordinal))
            {
                ApplyStyle(node, node.Id == "health-yes" ? "green" : node.Id is "health-no" or "health-align" or "health-visibility" or "health-track" ? "red" : "orange", node.Type == NodeType.Gateway ? "" : "step", "vertical");
            }
            else if (node.Id.StartsWith("learn-", StringComparison.Ordinal))
            {
                ApplyStyle(node, "purple", "step", "horizontal");
            }
        }
        var sectionGroups = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["direct-"] = "direct-frame",
            ["proactive-"] = "proactive-frame",
            ["milestone-"] = "milestone-frame",
            ["health-"] = "health-frame",
            ["learn-"] = "improvement-frame"
        };
        foreach (var node in flow.Nodes.Where(node => node.Type != NodeType.Section))
        {
            var section = sectionGroups.FirstOrDefault(group => node.Id.StartsWith(group.Key, StringComparison.Ordinal));
            if (!string.IsNullOrEmpty(section.Value)) node.CustomProperties["sectionId"] = section.Value;
        }
        flow.Connections =
        [
            Edge("support-edge-1", "support-core", "direct-1"),
            Edge("support-edge-2", "support-core", "proactive-1"),
            Edge("support-edge-3", "support-core", "milestone-1"),
            Edge("support-edge-4", "support-core", "health-1"),

            Edge("direct-edge-1", "direct-1", "direct-2"),
            Edge("direct-edge-2", "direct-2", "direct-3"),
            Edge("direct-edge-3", "direct-3", "direct-4"),
            Edge("direct-edge-4", "direct-4", "direct-5"),
            Edge("direct-edge-5", "direct-5", "direct-6"),

            Edge("proactive-edge-1", "proactive-1", "proactive-2"),
            Edge("proactive-edge-2", "proactive-2", "proactive-3"),
            Edge("proactive-edge-3", "proactive-3", "proactive-4"),
            Edge("proactive-edge-4", "proactive-4", "proactive-5"),
            Edge("proactive-edge-5", "proactive-5", "proactive-6"),
            Edge("proactive-edge-6", "proactive-6", "proactive-7"),

            Edge("milestone-edge-1", "milestone-1", "milestone-2"),
            Edge("milestone-edge-2", "milestone-2", "milestone-3"),
            Edge("milestone-edge-3", "milestone-3", "milestone-4"),
            Edge("milestone-edge-4", "milestone-4", "milestone-5"),
            Edge("milestone-edge-5", "milestone-5", "milestone-gate"),
            Edge("milestone-edge-6", "milestone-gate", "milestone-yes", "Yes", "output_1"),
            Edge("milestone-edge-7", "milestone-gate", "milestone-no", "No / Concern", "output_2"),
            Edge("milestone-edge-8", "milestone-yes", "milestone-6"),
            Edge("milestone-edge-9", "milestone-no", "milestone-6"),
            Edge("milestone-edge-10", "milestone-6", "milestone-7"),
            Edge("milestone-edge-11", "milestone-7", "milestone-8"),

            Edge("health-edge-1", "health-1", "health-2"),
            Edge("health-edge-2", "health-2", "health-3"),
            Edge("health-edge-3", "health-3", "health-4"),
            Edge("health-edge-4", "health-4", "health-gate"),
            Edge("health-edge-5", "health-gate", "health-yes", "Yes", "output_1"),
            Edge("health-edge-6", "health-gate", "health-no", "At Risk", "output_2"),
            Edge("health-edge-7", "health-no", "health-align"),
            Edge("health-edge-8", "health-align", "health-visibility"),
            Edge("health-edge-9", "health-visibility", "health-track"),

            Edge("learn-edge-1", "learn-1", "learn-2"),
            Edge("learn-edge-2", "learn-2", "learn-3"),
            Edge("learn-edge-3", "learn-3", "learn-4"),
            Edge("learn-edge-4", "learn-4", "learn-5"),
            Edge("learn-edge-5", "learn-5", "learn-6"),
            Edge("learn-edge-6", "learn-6", "learn-7"),
            Edge("learn-edge-7", "learn-7", "learn-8"),
            Edge("learn-edge-8", "learn-8", "learn-1", "Continuous Learning Cycle")
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

    private static FlowNode PresentationNode(
        string id,
        NodeType type,
        string title,
        string description,
        double x,
        double y,
        double width,
        double height,
        string tone,
        string presentationStyle = "card") => new()
    {
        Id = id,
        Type = type,
        Title = title,
        Description = description,
        X = x,
        Y = y,
        Width = width,
        Height = height,
        CustomProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tone"] = tone,
            ["presentationStyle"] = presentationStyle,
            ["layer"] = type == NodeType.Section ? "background" : "foreground",
            ["portLayout"] = type == NodeType.Section ? "vertical" : "horizontal"
        }
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
