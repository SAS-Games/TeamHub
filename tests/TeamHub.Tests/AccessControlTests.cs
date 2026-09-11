using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.Authentication;
using TeamHub.Web.AccessControl;

namespace TeamHub.Tests;

public sealed class AccessControlTests
{
    [Fact]
    public async Task BootstrapUser_IsImportedAndAuthenticatedFromAuthorizedList()
    {
        await using var host = await AccessTestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAccessService>();

        var authenticated = await users.ValidateCredentialsAsync("admin@example.com", "bootstrap123");
        await users.InitializeAsync();

        authenticated.Should().NotBeNull();
        authenticated!.UserType.Should().Be(TeamHubUserTypes.Admin);
        (await users.ListUsersAsync()).Should().ContainSingle(item => item.UserId == "admin@example.com");
    }

    [Fact]
    public async Task Registration_RequiresAnActiveAuthorizedRegisteredUser()
    {
        await using var host = await AccessTestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAccessService>();
        var authorized = await users.SaveUserAsync(new SaveAuthorizedUserRequest(
            null, "person@example.com", "Person", TeamHubUserTypes.Registered, true, null), "admin@example.com");

        (await users.RegisterAsync(new("unknown@example.com", "password123"))).Success.Should().BeFalse();
        (await users.ValidateCredentialsAsync("person@example.com", "password123")).Should().BeNull();
        (await users.RegisterAsync(new("person@example.com", "password123"))).Success.Should().BeTrue();
        (await users.ValidateCredentialsAsync("person@example.com", "password123")).Should().NotBeNull();

        await users.SetUserActiveAsync(authorized.Id, false, "admin@example.com");
        (await users.ValidateCredentialsAsync("person@example.com", "password123")).Should().BeNull();
    }

    [Fact]
    public async Task PermissionMatrix_UsesDefaultsAndPersistsAdminChanges()
    {
        await using var host = await AccessTestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAccessService>();

        (await users.GetAccessLevelAsync(TeamHubModules.Home, TeamHubUserTypes.Guest)).Should().Be(AccessLevel.ReadOnly);
        (await users.GetAccessLevelAsync(TeamHubModules.FlowDesigner, TeamHubUserTypes.Registered)).Should().Be(AccessLevel.Edit);
        (await users.GetAccessLevelAsync(TeamHubModules.AtlassianConnection, TeamHubUserTypes.Registered)).Should().Be(AccessLevel.Edit);

        await users.SetPermissionsAsync([
            new(TeamHubModules.Milestones, TeamHubUserTypes.Registered, AccessLevel.Create),
            new(TeamHubModules.UserManagement, TeamHubUserTypes.Privileged, AccessLevel.FullAccess),
            new(TeamHubModules.Home, TeamHubUserTypes.Admin, AccessLevel.NoAccess)
        ]);

        (await users.GetAccessLevelAsync(TeamHubModules.Milestones, TeamHubUserTypes.Registered)).Should().Be(AccessLevel.Create);
        (await users.GetAccessLevelAsync(TeamHubModules.UserManagement, TeamHubUserTypes.Privileged)).Should().Be(AccessLevel.NoAccess);
        (await users.GetAccessLevelAsync(TeamHubModules.Home, TeamHubUserTypes.Admin)).Should().Be(AccessLevel.FullAccess);
    }

    [Fact]
    public async Task DiscoveredModule_IsAddedWithFailClosedDefaultsAndCanBeConfigured()
    {
        await using var host = await AccessTestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAccessService>();

        await users.EnsureModulesAsync(["Reports"]);

        (await users.GetAccessLevelAsync("Reports", TeamHubUserTypes.Guest)).Should().Be(AccessLevel.NoAccess);
        (await users.GetAccessLevelAsync("Reports", TeamHubUserTypes.Registered)).Should().Be(AccessLevel.NoAccess);
        (await users.GetAccessLevelAsync("Reports", TeamHubUserTypes.Privileged)).Should().Be(AccessLevel.NoAccess);
        (await users.GetAccessLevelAsync("Reports", TeamHubUserTypes.Admin)).Should().Be(AccessLevel.FullAccess);

        await users.SetPermissionsAsync([new("Reports", TeamHubUserTypes.Registered, AccessLevel.ReadOnly)]);
        (await users.GetAccessLevelAsync("Reports", TeamHubUserTypes.Registered)).Should().Be(AccessLevel.ReadOnly);
    }

    [Theory]
    [InlineData("/Reports/Index", "Reports")]
    [InlineData("/ReleaseNotes", "Release Notes")]
    [InlineData("/Administration/Audit", "Administration")]
    [InlineData("/Register", null)]
    [InlineData("/Logout", null)]
    public void PageDiscovery_UsesFirstFolderAndPreservesSpecialModules(string pagePath, string? expected)
    {
        TeamHubModuleCatalog.ResolvePage(pagePath).Should().Be(expected);
    }

    [Fact]
    public void ModuleCatalog_DiscoversRazorPageEndpointMetadata()
    {
        var endpoint = new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/Reports"),
            0);
        endpoint.Metadata.Add(new PageActionDescriptor { ViewEnginePath = "/Reports/Index" });
        var catalog = new TeamHubModuleCatalog();

        catalog.Discover([new DefaultEndpointDataSource(endpoint.Build())]);

        catalog.Modules.Should().Contain("Reports");
    }

    [Theory]
    [InlineData("GET", "/Milestones", "", AccessLevel.ReadOnly)]
    [InlineData("GET", "/Flows/New", "", AccessLevel.Create)]
    [InlineData("POST", "/Studio/Configuration", "?handler=Delete", AccessLevel.Delete)]
    [InlineData("POST", "/api/flows/id/publish", "", AccessLevel.FullAccess)]
    public void RouteAccess_MapsOperationsToRequiredLevels(string method, string path, string query, AccessLevel expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);

        TeamHubAccessRoutes.ResolveRequiredAccess(context.Request).Should().Be(expected);
    }

    private sealed class AccessTestHost : IAsyncDisposable
    {
        private readonly string directory;
        public ServiceProvider Services { get; }

        private AccessTestHost(string directory, ServiceProvider services)
        {
            this.directory = directory;
            Services = services;
        }

        public static async Task<AccessTestHost> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"teamhub-access-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkflowUsers:0:Username"] = "admin@example.com",
                ["WorkflowUsers:0:Password"] = "bootstrap123",
                ["WorkflowUsers:0:Role"] = "Admin"
            }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddWorkflowAuthentication($"Data Source={Path.Combine(directory, "access.db")};Pooling=False");
            var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IUserAccessService>().InitializeAsync();
            return new AccessTestHost(directory, provider);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
