using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Serialization;
using TeamHub.FlowDesigner.Services;
using TeamHub.FlowDesigner.Validation;

namespace TeamHub.FlowDesigner.Tests;

public sealed class FlowServiceTests
{
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
