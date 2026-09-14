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
using TeamHub.FlowDesigner.Core.Validation;
using TeamHub.FlowDesigner.Serialization;
using TeamHub.FlowDesigner.Validation;
using TeamHub.FlowDesigner.Web.Pages.Flows;

namespace TeamHub.AI.FlowDesigner.Tests;

public sealed class CreateWithAiPageTests
{
    [Fact]
    public async Task Confirmation_creates_and_saves_through_flow_designer()
    {
        var proposal = BuildProposal();
        var generation = new StubGenerationWorkflow(new FlowDiagramGenerationDraft(
            proposal,
            [],
            [],
            "test",
            "model"));
        var flowService = new RecordingFlowService();
        var model = CreatePageModel(generation, flowService);
        model.Prompt = "Create the documented process.";
        model.Sources = [new AiFlowSourceInput { Title = "Process", Content = "Start, perform work, and finish." }];

        var previewResult = await model.OnPostGenerateAsync(CancellationToken.None);
        var confirmResult = await model.OnPostConfirmAsync(CancellationToken.None);

        previewResult.Should().BeOfType<PageResult>();
        model.ProtectedDraft.Should().NotBeNullOrWhiteSpace();
        confirmResult.Should().BeOfType<RedirectToPageResult>();
        flowService.CreateCalls.Should().Be(1);
        flowService.SaveCalls.Should().Be(1);
        flowService.SavedFlow.Should().NotBeNull();
        flowService.SavedFlow!.Nodes.Select(node => node.Id).Should().Equal("start", "work", "end");
        flowService.SavedFlow.CreatedBy.Should().Be("current-user");
        flowService.SavedFlow.IsShared.Should().BeFalse();
    }

    private static CreateWithAiModel CreatePageModel(
        IFlowDiagramGenerationWorkflow generation,
        IFlowService flows)
    {
        var page = new CreateWithAiModel(
            generation,
            flows,
            new SystemTextJsonFlowSerializer(),
            new FlowValidator(),
            new AllowCreatePermissionService(),
            Options.Create(new AiFlowDesignerOptions { Enabled = true }),
            new EphemeralDataProtectionProvider());
        page.PageContext = new PageContext { HttpContext = new DefaultHttpContext() };
        page.TempData = new TempDataDictionary(page.HttpContext, new DictionaryTempDataProvider());
        return page;
    }

    private static FlowDefinition BuildProposal() => new()
    {
        Name = "Generated process",
        Description = "Created from selected sources.",
        DiagramType = DiagramType.StandardFlowchart,
        Nodes =
        [
            new FlowNode { Id = "start", Type = NodeType.Start, Title = "Start" },
            new FlowNode { Id = "work", Type = NodeType.Process, Title = "Perform work" },
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

    private sealed class RecordingFlowService : IFlowService
    {
        public int CreateCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public FlowDefinition? SavedFlow { get; private set; }

        public Task<FlowDefinition> CreateAsync(
            string name,
            string? description = null,
            DiagramType diagramType = DiagramType.StandardFlowchart,
            FlowTemplate template = FlowTemplate.Blank,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            return Task.FromResult(new FlowDefinition
            {
                Id = Guid.NewGuid(),
                Name = name,
                Description = description ?? string.Empty,
                DiagramType = diagramType,
                CreatedBy = "current-user",
                IsShared = false
            });
        }

        public Task<FlowValidationResult> SaveAsync(FlowDefinition flow, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            SavedFlow = flow;
            return Task.FromResult(new FlowValidationResult());
        }

        public Task<IReadOnlyList<FlowSummary>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowSummary>> ListLinkTargetsAsync(Guid sourceFlowId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FlowDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NodeComment> AddNodeCommentAsync(Guid flowId, string nodeId, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RenameAsync(Guid id, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FlowDefinition> DuplicateAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
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