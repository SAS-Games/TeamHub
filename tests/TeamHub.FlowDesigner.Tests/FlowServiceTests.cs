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
    public async Task List_ShowsOnlyRootDiagramsWhileLinkTargetsIncludeNestedDiagrams()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        var service = new FlowService(repository, new FlowValidator(), new SystemTextJsonFlowSerializer(), new TestUserProvider(), new AllowAllPermissionService());
        var root = ValidationTests.ConnectedFlow();
        root.Name = "Root";
        var child = ValidationTests.ConnectedFlow();
        child.Name = "Child";
        var grandchild = ValidationTests.ConnectedFlow();
        grandchild.Name = "Grandchild";
        root.Nodes[0].ChildFlowId = child.Id;
        child.Nodes[0].ChildFlowId = grandchild.Id;
        await repository.SaveAsync(grandchild);
        await repository.SaveAsync(child);
        await repository.SaveAsync(root);

        var catalog = await service.ListAsync();
        var linkTargets = await service.ListLinkTargetsAsync(root.Id);

        Assert.Single(catalog);
        Assert.Equal(root.Id, catalog[0].Id);
        Assert.Equal(2, linkTargets.Count);
        Assert.Contains(linkTargets, flow => flow.Id == child.Id);
        Assert.Contains(linkTargets, flow => flow.Id == grandchild.Id);
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
    public async Task Save_AllowsAuthorToEditAndDeleteOwnComments()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        var service = new FlowService(repository, new FlowValidator(), new SystemTextJsonFlowSerializer(), new TestUserProvider(), new AllowAllPermissionService());
        var flow = ValidationTests.ConnectedFlow();
        flow.Nodes[0].Comments =
        [
            new NodeComment { Id = "edit", Author = "test", Body = "Before" },
            new NodeComment { Id = "delete", Author = "test", Body = "Remove me" }
        ];
        await repository.SaveAsync(flow);

        flow.Nodes[0].Comments[0].Body = "After";
        flow.Nodes[0].Comments[0].IsPublic = true;
        flow.Nodes[0].Comments.RemoveAt(1);
        var result = await service.SaveAsync(flow);
        var restored = await repository.GetAsync(flow.Id);

        Assert.True(result.IsValid);
        Assert.Single(restored!.Nodes[0].Comments);
        Assert.Equal("After", restored.Nodes[0].Comments[0].Body);
        Assert.True(restored.Nodes[0].Comments[0].IsPublic);
    }

    [Fact]
    public async Task Save_RejectsEditingOrDeletingAnotherAuthorsComment()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        var service = new FlowService(repository, new FlowValidator(), new SystemTextJsonFlowSerializer(), new TestUserProvider(), new AllowAllPermissionService());
        var flow = ValidationTests.ConnectedFlow();
        flow.Nodes[0].Comments = [new NodeComment { Id = "alice-comment", Author = "alice", Body = "Original" }];
        await repository.SaveAsync(flow);

        var edited = await repository.GetAsync(flow.Id);
        edited!.Nodes[0].Comments[0].Body = "Changed";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(edited));

        var visibilityChanged = await repository.GetAsync(flow.Id);
        visibilityChanged!.Nodes[0].Comments[0].IsPublic = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(visibilityChanged));

        var deleted = await repository.GetAsync(flow.Id);
        deleted!.Nodes[0].Comments.Clear();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(deleted));

        var restored = await repository.GetAsync(flow.Id);
        Assert.Equal("Original", restored!.Nodes[0].Comments.Single().Body);
    }

    [Fact]
    public async Task Save_AssignsAuthenticatedAuthorToNewComments()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        var service = new FlowService(repository, new FlowValidator(), new SystemTextJsonFlowSerializer(), new TestUserProvider(), new AllowAllPermissionService());
        var flow = ValidationTests.ConnectedFlow();
        await repository.SaveAsync(flow);
        flow.Nodes[0].Comments = [new NodeComment { Author = "admin", Body = "New comment" }];

        var result = await service.SaveAsync(flow);
        var restored = await repository.GetAsync(flow.Id);

        Assert.True(result.IsValid);
        Assert.Equal("test", restored!.Nodes[0].Comments.Single().Author);
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

    [Fact]
    public async Task Save_RejectsMissingChildDiagram()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        var service = new FlowService(repository, new FlowValidator(), new SystemTextJsonFlowSerializer(), new TestUserProvider(), new AllowAllPermissionService());
        var parent = ValidationTests.ConnectedFlow();
        await repository.SaveAsync(parent);
        parent.Nodes[0].ChildFlowId = Guid.NewGuid();

        var result = await service.SaveAsync(parent);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "child-flow-missing");
    }

    [Fact]
    public async Task Save_RejectsCycleAcrossLinkedDiagrams()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        var service = new FlowService(repository, new FlowValidator(), new SystemTextJsonFlowSerializer(), new TestUserProvider(), new AllowAllPermissionService());
        var parent = ValidationTests.ConnectedFlow();
        var child = ValidationTests.ConnectedFlow();
        child.Nodes[0].ChildFlowId = parent.Id;
        await repository.SaveAsync(parent);
        await repository.SaveAsync(child);
        parent.Nodes[0].ChildFlowId = child.Id;

        var result = await service.SaveAsync(parent);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "child-flow-cycle");
    }

    [Fact]
    public async Task Delete_AllowsCreatorToDiscardOnlyOwnUnsavedDraft()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        var service = new FlowService(repository, new FlowValidator(), new SystemTextJsonFlowSerializer(), new TestUserProvider(), new CreateOnlyPermissionService());
        var draft = await service.CreateAsync("Discard me");
        var saved = ValidationTests.ConnectedFlow();
        saved.CreatedBy = "test";
        await repository.SaveAsync(saved);

        await service.DeleteAsync(draft.Id);

        Assert.Null(await repository.GetAsync(draft.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteAsync(saved.Id));
        Assert.NotNull(await repository.GetAsync(saved.Id));
    }

    [Fact]
    public async Task Delete_RejectsDiagramReferencedByParent()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        var service = new FlowService(repository, new FlowValidator(), new SystemTextJsonFlowSerializer(), new TestUserProvider(), new AllowAllPermissionService());
        var child = ValidationTests.ConnectedFlow();
        var parent = ValidationTests.ConnectedFlow();
        parent.Nodes[0].ChildFlowId = child.Id;
        await repository.SaveAsync(child);
        await repository.SaveAsync(parent);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(child.Id));

        Assert.Contains("linked from another diagram", error.Message);
        Assert.NotNull(await repository.GetAsync(child.Id));
    }

    private sealed class TestUserProvider : ICurrentUserProvider
    {
        public string GetCurrentUserId() => "test";
    }

    private sealed class AllowAllPermissionService : IFlowPermissionService
    {
        public bool CanView(string? ownerId) => true;
        public bool CanViewShared() => true;
        public bool CanEdit(string? ownerId) => true;
        public bool CanDelete(string? ownerId) => true;
        public bool CanCreate() => true;
        public bool CanUseTemplate(FlowTemplate template) => true;
        public bool CanUseDiagramType(DiagramType diagramType) => true;
        public bool CanManageTemplates() => true;
        public bool CanReviewPublications() => true;
    }

    private sealed class CreateOnlyPermissionService : IFlowPermissionService
    {
        public bool CanView(string? ownerId) => string.Equals(ownerId, "test", StringComparison.OrdinalIgnoreCase);
        public bool CanViewShared() => false;
        public bool CanEdit(string? ownerId) => false;
        public bool CanDelete(string? ownerId) => false;
        public bool CanCreate() => true;
        public bool CanUseTemplate(FlowTemplate template) => true;
        public bool CanUseDiagramType(DiagramType diagramType) => true;
        public bool CanManageTemplates() => false;
        public bool CanReviewPublications() => false;
    }
}
