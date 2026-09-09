using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;
using TeamHub.FlowDesigner.Serialization;
using TeamHub.FlowDesigner.Services;
using TeamHub.FlowDesigner.Validation;

namespace TeamHub.FlowDesigner.Tests;

public sealed class FlowServiceTests
{
    [Fact]
    public async Task Create_PersistsRequestedDiagramTypeWithoutWorkflowMetadata()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        var service = new FlowService(repository, new FlowValidator(), new SystemTextJsonFlowSerializer(), new TestUserProvider(), new AllowAllPermissionService());

        var flow = await service.CreateAsync("Algorithm", diagramType: DiagramType.CodeFlow);

        Assert.Equal(DiagramType.CodeFlow, flow.DiagramType);
        Assert.Empty(flow.Nodes);
        Assert.Equal(DiagramType.CodeFlow, (await repository.GetAsync(flow.Id))!.DiagramType);
    }

    [Fact]
    public async Task Create_RemainsHiddenUntilFirstExplicitSave()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        var service = new FlowService(repository, new FlowValidator(), new SystemTextJsonFlowSerializer(), new TestUserProvider(), new AllowAllPermissionService());

        var draft = await service.CreateAsync("Unpublished diagram", diagramType: DiagramType.BusinessWorkflow);

        Assert.Equal(0, draft.Version);
        Assert.Empty(await service.ListAsync());

        var result = await service.SaveAsync(draft);

        Assert.True(result.IsValid);
        Assert.Equal(1, draft.Version);
        Assert.Contains(await service.ListAsync(), flow => flow.Id == draft.Id);
    }

    [Fact]
    public async Task Create_IntegrationQaTemplateBuildsCompleteExample()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        var validator = new FlowValidator();
        var service = new FlowService(repository, validator, new SystemTextJsonFlowSerializer(), new TestUserProvider(), new AllowAllPermissionService());

        var flow = await service.CreateAsync("Integration QA", template: FlowTemplate.IntegrationQa);

        Assert.Equal(DiagramType.StandardFlowchart, flow.DiagramType);
        Assert.Equal(12, flow.Nodes.Count);
        Assert.Equal(13, flow.Connections.Count);
        Assert.Contains(flow.Nodes, node => node.Type == NodeType.Decision && node.Comments.Count == 1);
        Assert.Empty(validator.Validate(flow).Issues);
    }

    [Fact]
    public async Task Create_StudioSupportTemplateBuildsEditableOperatingModel()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        var validator = new FlowValidator();
        var service = new FlowService(repository, validator, new SystemTextJsonFlowSerializer(), new TestUserProvider(), new AllowAllPermissionService());

        var flow = await service.CreateAsync("Studio Support", template: FlowTemplate.StudioSupport);

        Assert.Equal(DiagramType.BusinessWorkflow, flow.DiagramType);
        Assert.Equal(58, flow.Nodes.Count);
        Assert.Equal(43, flow.Connections.Count);
        Assert.Equal(5, flow.Nodes.Count(node => node.Type == NodeType.Section));
        Assert.Equal(10, flow.Nodes.Count(node => node.Type == NodeType.Annotation));
        Assert.DoesNotContain(flow.Nodes, node => node.Type is NodeType.Start or NodeType.End);
        Assert.Contains(flow.Nodes, node => node.Id == "support-title" && node.CustomProperties["presentationStyle"] == "banner");
        Assert.Contains(flow.Nodes, node => node.Id == "direct-frame" && node.Width == 330 && node.Height == 1050 && node.CustomProperties["presentationStyle"] == "band" && node.CustomProperties["portLayout"] == "vertical");
        Assert.Contains(flow.Nodes, node => node.Id == "health-frame" && node.CustomProperties["tone"] == "orange");
        Assert.Contains(flow.Nodes, node => node.Id == "direct-1" && node.CustomProperties["portLayout"] == "vertical" && node.CustomProperties["sectionId"] == "direct-frame");
        Assert.Contains(flow.Nodes, node => node.Id == "learn-1" && node.CustomProperties["portLayout"] == "horizontal");
        Assert.Equal(44, flow.Nodes.Count(node => node.CustomProperties.ContainsKey("sectionId")));
        Assert.Empty(validator.Validate(flow).Issues);
    }

    [Fact]
    public async Task Create_OnboardingTemplateBuildsParallelExecutableLayout()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        var validator = new FlowValidator();
        var service = new FlowService(repository, validator, new SystemTextJsonFlowSerializer(), new TestUserProvider(), new AllowAllPermissionService());

        var flow = await service.CreateAsync("Employee Onboarding", template: FlowTemplate.Onboarding);

        Assert.Equal(DiagramType.WorkCenterWorkflow, flow.DiagramType);
        Assert.Equal("ONBOARDING", flow.Metadata["workflowKey"]);
        Assert.Equal(4, flow.Nodes.Count(node => node.Type == NodeType.Activity));
        Assert.Equal(2, flow.Nodes.Count(node => node.Type == NodeType.ParallelGateway));
        Assert.Equal("48", flow.Nodes.Single(node => node.Id == "onboarding-approval").CustomProperties["expectedDurationHours"]);
        Assert.Contains(flow.Connections, edge => edge.SourceNodeId == "onboarding-split" && edge.TargetNodeId == "onboarding-security");
        Assert.Contains(flow.Connections, edge => edge.SourceNodeId == "onboarding-approval" && edge.TargetNodeId == "onboarding-lab");
        Assert.Empty(validator.Validate(flow).Issues);
    }

    [Fact]
    public async Task AddNodeComment_RecordsAuthenticatedAuthorAndPersists()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        var service = new FlowService(repository, new FlowValidator(), new SystemTextJsonFlowSerializer(), new TestUserProvider(), new AllowAllPermissionService());
        var flow = ValidationTests.ConnectedFlow();
        await repository.SaveAsync(flow);

        var comment = await service.AddNodeCommentAsync(flow.Id, "start", "Check the entry criteria.");
        var restored = await repository.GetAsync(flow.Id);

        Assert.Equal("test", comment.Author);
        Assert.Equal("Check the entry criteria.", restored!.Nodes.Single(node => node.Id == "start").Comments.Single().Body);
    }

    [Fact]
    public async Task Duplicate_CreatesIndependentFlowWithSameGraph()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        var serializer = new SystemTextJsonFlowSerializer();
        var service = new FlowService(repository, new FlowValidator(), serializer, new TestUserProvider(), new AllowAllPermissionService());
        var source = ValidationTests.ConnectedFlow();
        await repository.SaveAsync(source);

        var copy = await service.DuplicateAsync(source.Id);

        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal("Connected (copy)", copy.Name);
        Assert.Equal(0, copy.Version);
        Assert.Equal(source.Nodes.Select(node => node.Id), copy.Nodes.Select(node => node.Id));
        Assert.NotNull(await repository.GetAsync(copy.Id));
    }

    private sealed class TestUserProvider : ICurrentUserProvider
    {
        public string GetCurrentUserId() => "test";
    }

    private sealed class AllowAllPermissionService : IFlowPermissionService
    {
        public bool CanView(string? ownerId) => true;
        public bool CanEdit(string? ownerId) => true;
        public bool CanCreate() => true;
        public bool CanUseTemplate(FlowTemplate template) => true;
        public bool CanUseDiagramType(DiagramType diagramType) => true;
        public bool CanManageTemplates() => true;
    }
}
