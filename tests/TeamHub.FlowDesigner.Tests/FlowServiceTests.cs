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
    public async Task Create_IntegrationQaTemplateBuildsCompleteExample()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        var validator = new FlowValidator();
        var service = new FlowService(repository, validator, new SystemTextJsonFlowSerializer(), new TestUserProvider(), new AllowAllPermissionService());

        var flow = await service.CreateAsync("Integration QA", template: FlowTemplate.IntegrationQa);

        Assert.Equal(DiagramType.StandardFlowchart, flow.DiagramType);
        Assert.Equal(13, flow.Nodes.Count);
        Assert.Equal(13, flow.Connections.Count);
        Assert.Contains(flow.Nodes, node => node.Type == NodeType.Decision && node.Comments.Count == 1);
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
    }
}
