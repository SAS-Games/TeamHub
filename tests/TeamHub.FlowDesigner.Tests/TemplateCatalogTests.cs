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

        Assert.Equal(3, catalog.Count);
        Assert.Contains(catalog, item => item.TemplateKey == "ONBOARDING" && item.DiagramType == DiagramType.WorkCenterWorkflow);
        Assert.Equal(8, flow.Nodes.Count);
        Assert.Equal(8, flow.Connections.Count);
        Assert.Equal(0, flow.Version);
        Assert.DoesNotContain(await flows.ListAsync(), item => item.Id == flow.Id);
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
