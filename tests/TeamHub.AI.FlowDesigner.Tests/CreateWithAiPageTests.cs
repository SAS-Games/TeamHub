using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Options;
using TeamHub.AI.FlowDesigner.Contracts;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;
using TeamHub.FlowDesigner.Serialization;
using TeamHub.FlowDesigner.Web.Pages.Flows;

namespace TeamHub.AI.FlowDesigner.Tests;

public sealed class CreateWithAiPageTests
{
    [Fact]
    public async Task Confirmation_creates_the_complete_hierarchy_through_flow_designer()
    {
        var root = BuildFlow("Root", NodeType.Subprocess);
        var child = BuildFlow("Child", NodeType.Process);
        root.Nodes.Single(node => node.Id == "work").ChildFlowId = child.Id;
        var generation = new StubGenerationWorkflow(new FlowDiagramGenerationDraft(
            root, [], [], "test", "model") { ChildDiagrams = [child] });
        var hierarchies = new RecordingHierarchyService();
        var model = CreatePageModel(generation, hierarchies);
        model.Prompt = "Create the documented process hierarchy.";
        model.Sources = [new AiFlowSourceInput { Title = "Process", Content = "Start, perform work, and finish." }];

        var previewResult = await model.OnPostGenerateAsync(CancellationToken.None);
        var confirmResult = await model.OnPostConfirmAsync(CancellationToken.None);

        previewResult.Should().BeOfType<PageResult>();
        model.ProtectedDraft.Should().NotBeNullOrWhiteSpace();
        confirmResult.Should().BeOfType<RedirectToPageResult>();
        hierarchies.CreateCalls.Should().Be(1);
        hierarchies.Proposal!.Flows.Should().HaveCount(2);
        hierarchies.Proposal.Flows.Single(flow => flow.Id == root.Id)
            .Nodes.Single(node => node.Id == "work").ChildFlowId.Should().Be(child.Id);
    }

    private static CreateWithAiModel CreatePageModel(
        IFlowDiagramGenerationWorkflow generation,
        IFlowHierarchyService hierarchies)
    {
        var page = new CreateWithAiModel(
            generation,
            hierarchies,
            new SystemTextJsonFlowSerializer(),
            new AllowCreatePermissionService(),
            Options.Create(new AiFlowDesignerOptions { Enabled = true }),
            new EphemeralDataProtectionProvider());
        page.PageContext = new PageContext { HttpContext = new DefaultHttpContext() };
        page.TempData = new TempDataDictionary(page.HttpContext, new DictionaryTempDataProvider());
        return page;
    }

    private static FlowDefinition BuildFlow(string name, NodeType middleType) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Description = "Created from selected sources.",
        DiagramType = DiagramType.StandardFlowchart,
        Nodes =
        [
            new FlowNode { Id = "start", Type = NodeType.Start, Title = "Start" },
            new FlowNode { Id = "work", Type = middleType, Title = "Perform work" },
            new FlowNode { Id = "end", Type = NodeType.End, Title = "End" }
        ],
        Connections =
        [
            new FlowConnection { Id = "c1", SourceNodeId = "start", TargetNodeId = "work", SourcePort = "output_1", TargetPort = "input_1" },
            new FlowConnection { Id = "c2", SourceNodeId = "work", TargetNodeId = "end", SourcePort = "output_1", TargetPort = "input_1" }
        ],
        Metadata = new Dictionary<string, string> { ["ai.generated"] = "true" }
    };

    private sealed class StubGenerationWorkflow(FlowDiagramGenerationDraft draft) : IFlowDiagramGenerationWorkflow
    {
        public Task<FlowDiagramGenerationDraft> CreateDraftAsync(
            CreateFlowDiagramDraftRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(draft);
    }

    private sealed class RecordingHierarchyService : IFlowHierarchyService
    {
        public int CreateCalls { get; private set; }
        public FlowDiagramTemplateBundle? Proposal { get; private set; }

        public Task<FlowDiagramTemplateBundle> CreateDraftAsync(
            FlowDiagramTemplateBundle proposal,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            Proposal = proposal;
            return Task.FromResult(proposal);
        }

        public Task DeleteDraftAsync(Guid rootFlowId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class AllowCreatePermissionService : IFlowPermissionService
    {
        public bool CanView(string? ownerId) => true;
        public bool CanViewShared() => true;
        public bool CanEdit(string? ownerId) => true;
        public bool CanDelete(string? ownerId) => true;
        public bool CanCreate() => true;
        public bool CanUseTemplate(FlowTemplate template) => true;
        public bool CanUseDiagramType(DiagramType diagramType) => true;
        public bool CanManageTemplates() => false;
        public bool CanReviewPublications() => false;
    }

    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        private Dictionary<string, object> values = [];
        public IDictionary<string, object> LoadTempData(HttpContext context) => values;
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) =>
            this.values = new Dictionary<string, object>(values);
    }
}