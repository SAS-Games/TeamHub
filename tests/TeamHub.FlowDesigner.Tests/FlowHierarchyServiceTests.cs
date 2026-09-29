using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;
using TeamHub.FlowDesigner.DependencyInjection;
using TeamHub.FlowDesigner.Validation;

namespace TeamHub.FlowDesigner.Tests;

public sealed class FlowHierarchyServiceTests
{
    [Fact]
    public async Task Creates_root_and_children_atomically_with_real_links_and_owner()
    {
        await using var fixture = await HierarchyFixture.CreateAsync();
        var proposal = BuildHierarchy(linkChild: true);

        var created = await fixture.Hierarchies.CreateDraftAsync(proposal);

        var definitions = await fixture.Repository.ListDefinitionsAsync();
        Assert.Equal(2, definitions.Count);
        Assert.NotEqual(proposal.RootFlowId, created.RootFlowId);
        Assert.All(definitions, flow =>
        {
            Assert.Equal("hierarchy-user", flow.CreatedBy);
            Assert.False(flow.IsShared);
            Assert.Equal(1, flow.Version);
        });
        var root = definitions.Single(flow => flow.Id == created.RootFlowId);
        var childId = root.Nodes.Single(node => node.Id == "work").ChildFlowId;
        Assert.NotNull(childId);
        Assert.Contains(definitions, flow => flow.Id == childId);
    }

    [Fact]
    public async Task Deleting_the_root_removes_the_complete_hierarchy()
    {
        await using var fixture = await HierarchyFixture.CreateAsync();
        var created = await fixture.Hierarchies.CreateDraftAsync(BuildHierarchy(linkChild: true));

        await fixture.Hierarchies.DeleteDraftAsync(created.RootFlowId);

        Assert.Empty(await fixture.Repository.ListDefinitionsAsync());
    }

    [Fact]
    public async Task Direct_child_deletion_is_rejected_without_removing_anything()
    {
        await using var fixture = await HierarchyFixture.CreateAsync();
        var created = await fixture.Hierarchies.CreateDraftAsync(BuildHierarchy(linkChild: true));
        var childId = created.Flows.Single(flow => flow.Id != created.RootFlowId).Id;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Hierarchies.DeleteDraftAsync(childId));

        Assert.Equal(2, (await fixture.Repository.ListDefinitionsAsync()).Count);
    }
    [Fact]
    public async Task Rejects_an_orphan_without_writing_any_diagram()
    {
        await using var fixture = await HierarchyFixture.CreateAsync();
        var proposal = BuildHierarchy(linkChild: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Hierarchies.CreateDraftAsync(proposal));

        Assert.Empty(await fixture.Repository.ListDefinitionsAsync());
    }

    [Fact]
    public void Rejects_a_child_linked_more_than_once()
    {
        var hierarchy = BuildHierarchy(linkChild: true);
        var root = hierarchy.Flows.Single(flow => flow.Id == hierarchy.RootFlowId);
        root.Nodes.Single(node => node.Id == "end").ChildFlowId = hierarchy.Flows.Single(flow => flow.Id != root.Id).Id;

        var result = new FlowHierarchyValidator().Validate(hierarchy);

        Assert.Contains(result.Issues, issue => issue.Code == "hierarchy-multiple-parents");
    }

    [Fact]
    public void Rejects_a_parent_child_cycle()
    {
        var hierarchy = BuildHierarchy(linkChild: true);
        var root = hierarchy.Flows.Single(flow => flow.Id == hierarchy.RootFlowId);
        var child = hierarchy.Flows.Single(flow => flow.Id != root.Id);
        child.Nodes.Single(node => node.Id == "work").ChildFlowId = root.Id;

        var result = new FlowHierarchyValidator().Validate(hierarchy);

        Assert.Contains(result.Issues, issue => issue.Code == "hierarchy-cycle");
    }
    private static FlowDiagramTemplateBundle BuildHierarchy(bool linkChild)
    {
        var root = BuildFlow("Root", NodeType.Subprocess);
        var child = BuildFlow("Child", NodeType.Process);
        if (linkChild) root.Nodes.Single(node => node.Id == "work").ChildFlowId = child.Id;
        return new FlowDiagramTemplateBundle { RootFlowId = root.Id, Flows = [root, child] };
    }

    private static FlowDefinition BuildFlow(string name, NodeType middleType) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Description = "Hierarchy test.",
        DiagramType = DiagramType.StandardFlowchart,
        Nodes =
        [
            new FlowNode { Id = "start", Type = NodeType.Start, Title = "Start" },
            new FlowNode { Id = "work", Type = middleType, Title = "Work" },
            new FlowNode { Id = "end", Type = NodeType.End, Title = "End" }
        ],
        Connections =
        [
            new FlowConnection { Id = "c1", SourceNodeId = "start", TargetNodeId = "work", SourcePort = "output_1", TargetPort = "input_1" },
            new FlowConnection { Id = "c2", SourceNodeId = "work", TargetNodeId = "end", SourcePort = "output_1", TargetPort = "input_1" }
        ]
    };

    private sealed class HierarchyFixture : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly string directory;

        private HierarchyFixture(ServiceProvider provider, string directory)
        {
            this.provider = provider;
            this.directory = directory;
        }

        public IFlowHierarchyService Hierarchies => provider.GetRequiredService<IFlowHierarchyService>();
        public IFlowRepository Repository => provider.GetRequiredService<IFlowRepository>();

        public static async Task<HierarchyFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"teamhub-flow-hierarchy-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var services = new ServiceCollection();
            services.AddSingleton<ICurrentUserProvider>(new TestCurrentUserProvider());
            services.AddSingleton<IFlowPermissionService>(new TestPermissionService());
            services.AddFlowDesigner(options =>
            {
                options.ConnectionString = $"Data Source={Path.Combine(directory, "flows.db")}";
                options.LibraryConnectionString = $"Data Source={Path.Combine(directory, "flow-library.db")}";
            });
            var provider = services.BuildServiceProvider();
            await provider.InitializeFlowDesignerAsync();
            return new HierarchyFixture(provider, directory);
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TestCurrentUserProvider : ICurrentUserProvider
    {
        public string? GetCurrentUserId() => "hierarchy-user";
    }

    private sealed class TestPermissionService : IFlowPermissionService
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
}
