using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using TeamHub.Application.Interfaces;
using TeamHub.Application.Models;
using TeamHub.FlowDesigner.Core.Models;
using TeamHub.Web.WorkCenter;

namespace TeamHub.Tests;

public sealed class WorkCenterFlowPublicationTests
{
    [Fact]
    public async Task Publish_ConvertsParallelDiagramIntoTaskDependencies()
    {
        var configuration = new RecordingWorkflowConfigurationService();
        var publisher = new WorkCenterFlowPublicationService(configuration, AdminContext());
        var flow = OnboardingFlow();

        var result = await publisher.PublishAsync(flow);

        result.Success.Should().BeTrue();
        configuration.SavedDraft.Should().NotBeNull();
        var steps = configuration.SavedDraft!.Steps.ToDictionary(step => step.StepKey);
        steps["CREATE_EOO_TICKET"].DependsOnCsv.Should().BeEmpty();
        steps["PIC_UAT_APPROVAL"].DependsOnCsv.Should().Be("CREATE_EOO_TICKET");
        steps["SECURITY_BRIEFING"].DependsOnCsv.Should().Be("CREATE_EOO_TICKET");
        steps["LAB_ACCESS"].DependsOnCsv.Should().Be("PIC_UAT_APPROVAL");
        steps["PIC_UAT_APPROVAL"].ExpectedDurationHours.Should().Be(48);
        configuration.PublishedBy.Should().Be("admin");
    }

    [Fact]
    public async Task Publish_RejectsUnsupportedDecisionNode()
    {
        var configuration = new RecordingWorkflowConfigurationService();
        var publisher = new WorkCenterFlowPublicationService(configuration, AdminContext());
        var flow = OnboardingFlow();
        flow.Nodes.Add(Node("decision", NodeType.Decision, "Approved?", "PIC"));

        var result = await publisher.PublishAsync(flow);

        result.Success.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("cannot execute yet"));
        configuration.SavedDraft.Should().BeNull();
    }

    private static FlowDefinition OnboardingFlow()
    {
        return new FlowDefinition
        {
            Name = "Employee Onboarding",
            DiagramType = DiagramType.WorkCenterWorkflow,
            Version = 1,
            Metadata = new Dictionary<string, string> { ["workflowKey"] = "ONBOARDING", ["enabled"] = "true" },
            Nodes =
            [
                Node("start", NodeType.Start, "Start"),
                Node("eoo", NodeType.Activity, "Create an EOO ticket", "Coordinator", 24, "CREATE_EOO_TICKET"),
                Node("split", NodeType.ParallelGateway, "Parallel"),
                Node("approval", NodeType.Activity, "Approval from PIC UAT", "PIC", 48, "PIC_UAT_APPROVAL"),
                Node("security", NodeType.Activity, "Security Briefing", "Security", 24, "SECURITY_BRIEFING"),
                Node("lab", NodeType.Activity, "Lab Access", "Lab Admin", 24, "LAB_ACCESS"),
                Node("join", NodeType.ParallelGateway, "Join"),
                Node("end", NodeType.End, "End")
            ],
            Connections =
            [
                Edge("start", "eoo"),
                Edge("eoo", "split"),
                Edge("split", "approval"),
                Edge("split", "security"),
                Edge("approval", "lab"),
                Edge("lab", "join"),
                Edge("security", "join"),
                Edge("join", "end")
            ]
        };
    }

    private static FlowNode Node(
        string id,
        NodeType type,
        string title,
        string owner = "",
        double expectedHours = 24,
        string? stepKey = null) => new()
    {
        Id = id,
        Type = type,
        Title = title,
        CustomProperties = new Dictionary<string, string>
        {
            ["owner"] = owner,
            ["ownerType"] = "User",
            ["expectedDurationHours"] = expectedHours.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["stepKey"] = stepKey ?? ""
        }
    };

    private static FlowConnection Edge(string source, string target) => new()
    {
        SourceNodeId = source,
        TargetNodeId = target
    };

    private static IHttpContextAccessor AdminContext()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "admin"),
            new Claim(ClaimTypes.Role, "Admin")
        ], "Test");
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private sealed class RecordingWorkflowConfigurationService : IWorkflowConfigurationService
    {
        public WorkflowDraftDto? SavedDraft { get; private set; }
        public string? PublishedBy { get; private set; }

        public Task<Guid> SaveDraftAsync(WorkflowDraftDto draft, CancellationToken cancellationToken = default)
        {
            SavedDraft = draft;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<ConfigurationSyncResult> PublishDraftAsync(Guid id, string actor, CancellationToken cancellationToken = default)
        {
            PublishedBy = actor;
            return Task.FromResult(new ConfigurationSyncResult
            {
                Success = true,
                ImportedWorkflowSummaries = ["ONBOARDING: v1"]
            });
        }

        public Task<ConfigurationSyncResult> SyncAsync(string actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowDraftSummaryDto>> GetDraftsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkflowDraftDto?> GetDraftAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteDraftAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
