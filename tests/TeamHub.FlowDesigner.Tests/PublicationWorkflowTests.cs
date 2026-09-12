using Microsoft.EntityFrameworkCore;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;
using TeamHub.FlowDesigner.Serialization;
using TeamHub.FlowDesigner.Services;
using TeamHub.FlowDesigner.Validation;

namespace TeamHub.FlowDesigner.Tests;

public sealed class PublicationWorkflowTests
{
    [Fact]
    public async Task Approval_CreatesIndependentImmutablePublishedSnapshot()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        await database.InitializePublishedStoreAsync();
        var flow = ValidationTests.ConnectedFlow();
        flow.CreatedBy = "alice";
        flow.Nodes[0].Comments = [new NodeComment { Author = "alice", Body = "Internal review note" }];
        await repository.SaveAsync(flow);
        var alice = CreateService(database, repository, "alice", isAdmin: false);
        var admin = CreateService(database, repository, "admin", isAdmin: true);

        var request = await alice.RequestAsync(flow.Id);
        var published = await admin.ApproveAsync(request.Id, "Approved for the company library.");
        await repository.DeleteAsync(flow.Id);
        await using (var publishedContext = await database.PublishedFactory.CreateDbContextAsync())
        {
            await publishedContext.Database.ExecuteSqlRawAsync("DELETE FROM PublishedDiagrams");
        }
        await new PublishedFlowRecoveryService(database.Factory, database.PublishedFactory, new SystemTextJsonFlowSerializer()).RecoverIfEmptyAsync();
        var restored = await admin.GetPublishedBySourceAsync(flow.Id);

        Assert.Equal(FlowPublicationRequestStatus.Pending, request.Status);
        Assert.Equal(1, published.PublicationVersion);
        Assert.NotNull(restored);
        Assert.Equal(flow.Name, restored!.Definition.Name);
        Assert.All(restored.Definition.Nodes, node => Assert.Empty(node.Comments));
        Assert.Null(await repository.GetAsync(flow.Id));
        Assert.Equal(FlowPublicationRequestStatus.Approved, (await admin.GetRequestAsync(request.Id))!.Summary.Status);
    }

    [Fact]
    public async Task Request_FreezesSubmittedVersionAndPreventsParallelPendingRequest()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        await database.InitializePublishedStoreAsync();
        var flow = ValidationTests.ConnectedFlow();
        flow.CreatedBy = "alice";
        await repository.SaveAsync(flow);
        var alice = CreateService(database, repository, "alice", isAdmin: false);
        var admin = CreateService(database, repository, "admin", isAdmin: true);

        var request = await alice.RequestAsync(flow.Id);
        flow.Name = "Changed after submission";
        flow.Version++;
        await repository.SaveAsync(flow);

        var frozen = await admin.GetRequestAsync(request.Id);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => alice.RequestAsync(flow.Id));

        Assert.Equal("Connected", frozen!.Snapshot.Name);
        Assert.Contains("already has a version", error.Message);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => alice.ListPendingAsync());
    }

    [Fact]
    public async Task Rejection_RequiresFeedbackAndAllowsARevisedRequest()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        await database.InitializePublishedStoreAsync();
        var flow = ValidationTests.ConnectedFlow();
        flow.CreatedBy = "alice";
        await repository.SaveAsync(flow);
        var alice = CreateService(database, repository, "alice", isAdmin: false);
        var admin = CreateService(database, repository, "admin", isAdmin: true);
        var request = await alice.RequestAsync(flow.Id);

        await Assert.ThrowsAsync<ArgumentException>(() => admin.RejectAsync(request.Id, ""));
        await admin.RejectAsync(request.Id, "Add the missing owner information.");
        flow.Version++;
        await repository.SaveAsync(flow);
        var revised = await alice.RequestAsync(flow.Id);

        Assert.NotEqual(request.Id, revised.Id);
        Assert.Equal(FlowPublicationRequestStatus.Rejected, (await admin.GetRequestAsync(request.Id))!.Summary.Status);
        Assert.Null(await admin.GetPublishedBySourceAsync(flow.Id));
    }

    [Fact]
    public async Task Reapproval_KeepsHistoryAndMakesOnlyLatestVersionCurrent()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        await database.InitializePublishedStoreAsync();
        var flow = ValidationTests.ConnectedFlow();
        flow.CreatedBy = "alice";
        await repository.SaveAsync(flow);
        var alice = CreateService(database, repository, "alice", isAdmin: false);
        var admin = CreateService(database, repository, "admin", isAdmin: true);

        var firstRequest = await alice.RequestAsync(flow.Id);
        await admin.ApproveAsync(firstRequest.Id, null);
        flow.Name = "Connected v2";
        flow.Version++;
        await repository.SaveAsync(flow);
        var secondRequest = await alice.RequestAsync(flow.Id);
        var secondPublication = await admin.ApproveAsync(secondRequest.Id, null);

        var catalog = await admin.ListPublishedAsync();
        var current = await admin.GetPublishedBySourceAsync(flow.Id);
        Assert.Single(catalog);
        Assert.Equal(2, secondPublication.PublicationVersion);
        Assert.Equal("Connected v2", current!.Definition.Name);
    }

    [Fact]
    public async Task Request_FromChildPublishesTheCompleteRecursiveRootHierarchy()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        await database.InitializePublishedStoreAsync();
        var parent = ValidationTests.ConnectedFlow();
        parent.CreatedBy = "alice";
        parent.Name = "Parent";
        var child = ValidationTests.ConnectedFlow();
        child.CreatedBy = "alice";
        child.Name = "Child";
        var grandchild = ValidationTests.ConnectedFlow();
        grandchild.CreatedBy = "alice";
        grandchild.Name = "Grandchild";
        parent.Nodes[0].ChildFlowId = child.Id;
        child.Nodes[0].ChildFlowId = grandchild.Id;
        await repository.SaveAsync(grandchild);
        await repository.SaveAsync(child);
        await repository.SaveAsync(parent);
        var alice = CreateService(database, repository, "alice", isAdmin: false);
        var admin = CreateService(database, repository, "admin", isAdmin: true);

        var request = await alice.RequestAsync(child.Id);
        var frozenChild = await admin.GetRequestDiagramAsync(request.Id, child.Id);
        var publication = await admin.ApproveAsync(request.Id, null);
        var catalog = await alice.ListPublishedAsync();
        var publishedParent = await alice.GetPublishedBySourceAsync(parent.Id);
        var publishedChild = await alice.GetPublishedBySourceAsync(child.Id);
        var publishedGrandchild = await alice.GetPublishedBySourceAsync(grandchild.Id);

        Assert.Equal(parent.Id, request.FlowId);
        Assert.Equal(parent.Nodes.Count + child.Nodes.Count + grandchild.Nodes.Count, request.NodeCount);
        Assert.Equal("Child", frozenChild!.Name);
        Assert.Equal(parent.Id, publication.SourceFlowId);
        Assert.Single(catalog);
        Assert.Equal(parent.Id, catalog[0].SourceFlowId);
        Assert.Equal("Parent", publishedParent!.Definition.Name);
        Assert.Equal("Child", publishedChild!.Definition.Name);
        Assert.Equal("Grandchild", publishedGrandchild!.Definition.Name);

        await using (var publishedContext = await database.PublishedFactory.CreateDbContextAsync())
        {
            await publishedContext.Database.ExecuteSqlRawAsync("DELETE FROM PublishedDiagrams");
        }
        await new PublishedFlowRecoveryService(
            database.Factory, database.PublishedFactory, new SystemTextJsonFlowSerializer()).RecoverIfEmptyAsync();
        Assert.Equal("Grandchild", (await alice.GetPublishedBySourceAsync(grandchild.Id))!.Definition.Name);
    }

    [Fact]
    public async Task Admin_CanEditAPublishedChildAndCreatesANewHierarchyVersion()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        await database.InitializePublishedStoreAsync();
        var parent = ValidationTests.ConnectedFlow();
        parent.CreatedBy = "alice";
        parent.Name = "Parent";
        var child = ValidationTests.ConnectedFlow();
        child.CreatedBy = "alice";
        child.Name = "Child";
        parent.Nodes[0].ChildFlowId = child.Id;
        await repository.SaveAsync(child);
        await repository.SaveAsync(parent);
        var alice = CreateService(database, repository, "alice", isAdmin: false);
        var admin = CreateService(database, repository, "admin", isAdmin: true);
        var request = await alice.RequestAsync(parent.Id);
        await admin.ApproveAsync(request.Id, null);
        var publishedChild = (await admin.GetPublishedBySourceAsync(child.Id))!.Definition;

        publishedChild.Name = "Child corrected by admin";
        var updated = await admin.UpdatePublishedAsync(child.Id, publishedChild);
        var catalog = await alice.ListPublishedAsync();
        var currentParent = await alice.GetPublishedBySourceAsync(parent.Id);
        var currentChild = await alice.GetPublishedBySourceAsync(child.Id);

        Assert.Equal("Child corrected by admin", updated.Name);
        Assert.Equal("Child corrected by admin", currentChild!.Definition.Name);
        Assert.Equal(2, currentParent!.Summary.PublicationVersion);
        Assert.Single(catalog);
        Assert.Equal(2, catalog[0].PublicationVersion);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => alice.UpdatePublishedAsync(child.Id, currentChild.Definition));
        currentChild.Definition.Nodes[0].ChildFlowId = parent.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => admin.UpdatePublishedAsync(child.Id, currentChild.Definition));
    }

    private static FlowPublicationWorkflowService CreateService(
        TestDatabase database,
        IFlowRepository repository,
        string userName,
        bool isAdmin)
    {
        var serializer = new SystemTextJsonFlowSerializer();
        return new FlowPublicationWorkflowService(
            database.Factory,
            database.PublishedFactory,
            repository,
            serializer,
            new FlowValidator(),
            new UserProvider(userName),
            new PermissionService(userName, isAdmin),
            new HostPublisher());
    }

    private sealed class UserProvider(string userName) : ICurrentUserProvider
    {
        public string GetCurrentUserId() => userName;
    }

    private sealed class PermissionService(string userName, bool isAdmin) : IFlowPermissionService
    {
        public bool CanView(string? ownerId) => isAdmin || string.Equals(ownerId, userName, StringComparison.OrdinalIgnoreCase);
        public bool CanViewShared() => false;
        public bool CanEdit(string? ownerId) => CanView(ownerId);
        public bool CanDelete(string? ownerId) => CanView(ownerId);
        public bool CanCreate() => true;
        public bool CanUseTemplate(FlowTemplate template) => true;
        public bool CanUseDiagramType(DiagramType diagramType) => true;
        public bool CanManageTemplates() => isAdmin;
        public bool CanReviewPublications() => isAdmin;
    }

    private sealed class HostPublisher : IFlowPublicationService
    {
        public bool CanPublish(FlowDefinition flow) => true;
        public Task<FlowPublicationResult> PublishAsync(FlowDefinition flow, CancellationToken cancellationToken = default) =>
            Task.FromResult(FlowPublicationResult.Published("Published"));
    }
}
