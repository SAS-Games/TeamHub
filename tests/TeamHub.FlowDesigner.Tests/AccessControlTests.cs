using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Serialization;
using TeamHub.FlowDesigner.Services;
using TeamHub.FlowDesigner.Validation;

namespace TeamHub.FlowDesigner.Tests;

public sealed class AccessControlTests
{
    [Fact]
    public async Task User_SeesAndChangesOnlyOwnedFlows()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        var aliceFlow = ValidationTests.ConnectedFlow();
        aliceFlow.Name = "Alice flow";
        aliceFlow.CreatedBy = "alice";
        var bobFlow = ValidationTests.ConnectedFlow();
        bobFlow.Id = Guid.NewGuid();
        bobFlow.Name = "Bob flow";
        bobFlow.CreatedBy = "bob";
        await repository.SaveAsync(aliceFlow);
        await repository.SaveAsync(bobFlow);

        var service = CreateService(repository, "alice", isAdmin: false);

        var visible = await service.ListAsync();
        Assert.Single(visible);
        Assert.Equal(aliceFlow.Id, visible[0].Id);
        Assert.Null(await service.GetAsync(bobFlow.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RenameAsync(bobFlow.Id, "Not allowed"));
    }

    [Fact]
    public async Task Admin_CanSeeAllFlowsIncludingLegacyUnownedFlows()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        var owned = ValidationTests.ConnectedFlow();
        owned.CreatedBy = "alice";
        var legacy = ValidationTests.ConnectedFlow();
        legacy.Id = Guid.NewGuid();
        legacy.CreatedBy = null;
        await repository.SaveAsync(owned);
        await repository.SaveAsync(legacy);

        var service = CreateService(repository, "admin", isAdmin: true);

        Assert.Equal(2, (await service.ListAsync()).Count);
        Assert.NotNull(await service.GetAsync(legacy.Id));
    }

    private static FlowService CreateService(IFlowRepository repository, string userName, bool isAdmin) =>
        new(repository, new FlowValidator(), new SystemTextJsonFlowSerializer(), new UserProvider(userName), new PermissionService(userName, isAdmin));

    private sealed class UserProvider(string userName) : ICurrentUserProvider
    {
        public string GetCurrentUserId() => userName;
    }

    private sealed class PermissionService(string userName, bool isAdmin) : IFlowPermissionService
    {
        public bool CanView(string? ownerId) => isAdmin || string.Equals(ownerId, userName, StringComparison.OrdinalIgnoreCase);
        public bool CanEdit(string? ownerId) => CanView(ownerId);
        public bool CanCreate() => true;
    }
}
