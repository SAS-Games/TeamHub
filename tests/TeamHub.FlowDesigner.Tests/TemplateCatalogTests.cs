using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;
using TeamHub.FlowDesigner.DependencyInjection;

namespace TeamHub.FlowDesigner.Tests;

public sealed class TemplateCatalogTests
{
    [Fact]
    public async Task Initialize_SeedsDatabaseBackedTemplatesAndCreatesHiddenDraft()
    {
        await using var host = await TemplateTestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var templates = scope.ServiceProvider.GetRequiredService<IFlowTemplateCatalogService>();
        var flows = scope.ServiceProvider.GetRequiredService<IFlowService>();

        var catalog = await templates.ListAsync();
        var flow = await templates.CreateFlowByKeyAsync("ONBOARDING");

        Assert.Equal(2, catalog.Count);
        Assert.DoesNotContain(catalog, item => item.TemplateKey == "STUDIO_SUPPORT");
        Assert.Contains(catalog, item => item.TemplateKey == "ONBOARDING" && item.DiagramType == DiagramType.WorkCenterWorkflow);
        Assert.Equal(8, flow.Nodes.Count);
        Assert.Equal(8, flow.Connections.Count);
        Assert.Equal(0, flow.Version);
        Assert.DoesNotContain(await flows.ListAsync(), item => item.Id == flow.Id);
    }

    [Fact]
    public async Task DeleteTemplate_HidesItAndDoesNotReseedIt()
    {
        await using var host = await TemplateTestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var templates = scope.ServiceProvider.GetRequiredService<IFlowTemplateCatalogService>();
        var template = (await templates.ListAsync()).Single(item => item.TemplateKey == "INTEGRATION_QA");

        await templates.DeleteAsync(template.Id);
        await host.Services.InitializeFlowDesignerAsync();

        Assert.DoesNotContain(await templates.ListAsync(), item => item.Id == template.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => templates.CreateFlowAsync(template.Id));
    }

    [Fact]
    public async Task SaveFlow_StoresReusableVersionedCatalogEntry()
    {
        await using var host = await TemplateTestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var templates = scope.ServiceProvider.GetRequiredService<IFlowTemplateCatalogService>();
        var flows = scope.ServiceProvider.GetRequiredService<IFlowService>();
        var source = await templates.CreateFlowByKeyAsync("INTEGRATION_QA", "Reusable QA");
        Assert.True((await flows.SaveAsync(source)).IsValid);

        var saved = await templates.SaveFlowAsync(source.Id, new SaveFlowTemplateRequest(
            "TEAM_QA",
            "Team QA",
            "Reusable team QA path.",
            "Quality"));
        var created = await templates.CreateFlowAsync(saved.Id, "Project QA");

        Assert.Equal("TEAM_QA", saved.TemplateKey);
        Assert.Equal(1, saved.Version);
        Assert.False(saved.IsBuiltIn);
        Assert.NotEqual(source.Id, created.Id);
        Assert.Equal("Project QA", created.Name);
        Assert.Equal(source.Nodes.Count, created.Nodes.Count);
        Assert.Equal(0, created.Version);
    }

    [Fact]
    public async Task CreateBundle_CreatesHierarchyWithFreshRemappedIds()
    {
        await using var host = await TemplateTestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var templates = scope.ServiceProvider.GetRequiredService<IFlowTemplateCatalogService>();
        var repository = scope.ServiceProvider.GetRequiredService<ITemplateCatalogRepository>();
        var flows = scope.ServiceProvider.GetRequiredService<IFlowService>();
        var flowRepository = scope.ServiceProvider.GetRequiredService<IFlowRepository>();
        var serializer = scope.ServiceProvider.GetRequiredService<IFlowSerializer>();
        var child = ValidationTests.ConnectedFlow();
        child.Name = "Child detail";
        var root = ValidationTests.ConnectedFlow();
        root.Name = "Root source";
        root.Nodes[0].ChildFlowId = child.Id;
        root.Nodes[0].Comments = [new NodeComment { Body = "Official source: https://example.test/spec" }];
        var bundle = new FlowDiagramTemplateBundle { RootFlowId = root.Id, Flows = [root, child] };
        var definition = new TemplateCatalogDefinition
        {
            TemplateKey = "BUNDLE_TEST",
            Name = "Hierarchy",
            Description = "A hierarchy template.",
            Category = "Tests",
            TemplateKind = TemplateKinds.FlowDiagramBundle,
            DiagramType = DiagramType.CodeFlow,
            PayloadJson = serializer.SerializeBundle(bundle),
            IsBuiltIn = true
        };
        await repository.SaveAsync(definition);

        var createdRoot = await templates.CreateFlowByKeyAsync("BUNDLE_TEST", "Created hierarchy");
        var createdDefinitions = await flowRepository.ListDefinitionsAsync();
        var createdChildId = createdRoot.Nodes[0].ChildFlowId;

        Assert.NotEqual(root.Id, createdRoot.Id);
        Assert.NotEqual(child.Id, createdChildId);
        Assert.Equal("Created hierarchy", createdRoot.Name);
        Assert.Contains("https://example.test/spec", createdRoot.Nodes[0].Comments[0].Body);
        Assert.Contains(createdDefinitions, flow => flow.Id == createdChildId && flow.Name == "Child detail");
        Assert.Equal(2, createdDefinitions.Count);
        Assert.Empty(await flows.ListAsync());
    }

    private sealed class TemplateTestHost : IAsyncDisposable
    {
        private readonly string directory;
        public ServiceProvider Services { get; }

        private TemplateTestHost(string directory, ServiceProvider services)
        {
            this.directory = directory;
            Services = services;
        }

        public static async Task<TemplateTestHost> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"teamhub-template-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var services = new ServiceCollection();
            services.AddFlowDesigner(options =>
            {
                options.ConnectionString = $"Data Source={Path.Combine(directory, "flows.db")};Pooling=False";
                options.TemplateConnectionString = $"Data Source={Path.Combine(directory, "templates.db")};Pooling=False";
            });
            var provider = services.BuildServiceProvider();
            await provider.InitializeFlowDesignerAsync();
            return new TemplateTestHost(directory, provider);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
