using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using TeamHub.Studio;
using TeamHub.Web.Confluence;

namespace TeamHub.Tests;

public sealed class ConfluenceBrowserTests
{
    [Fact]
    public async Task BrowserListsOnlyConfiguredRootDescendantsAndLoadsRenderedPage()
    {
        var service = CreateService(request => request.RequestUri!.AbsolutePath switch
        {
            "/rest/api/content/100" => Json("""
                {"id":"100","title":"Knowledge","ancestors":[],"_links":{"webui":"/pages/viewpage.action?pageId=100"}}
                """),
            "/rest/api/content/100/descendant/page" => Json("""
                {"results":[{"id":"101","title":"Build Guide","ancestors":[{"id":"100","title":"Knowledge"}],"_links":{"webui":"/pages/viewpage.action?pageId=101"}}],"size":1}
                """),
            "/rest/api/content/101" => Json("""
                {"id":"101","title":"Build Guide","ancestors":[{"id":"100","title":"Knowledge"}],"body":{"view":{"value":"<h2>Build</h2><table><tr><td>Ready</td></tr></table>"}},"_links":{"webui":"/pages/viewpage.action?pageId=101"}}
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var navigation = await service.GetNavigationAsync("100", "reader@example.com", false);
        var page = await service.GetPageAsync("100", "101", "reader@example.com", false);

        navigation.Pages.Should().HaveCount(2);
        navigation.Pages[0].Should().BeEquivalentTo(new
        {
            Id = "100", Title = "Knowledge", ParentId = "", Depth = 0
        });
        navigation.Pages[1].Should().BeEquivalentTo(new
        {
            Id = "101", Title = "Build Guide", ParentId = "100", Depth = 1
        });
        page.Succeeded.Should().BeTrue();
        page.RenderedHtml.Should().Contain("<table>");
        page.Breadcrumbs.Select(item => item.Title).Should().Equal("Knowledge", "Build Guide");
    }

    [Fact]
    public async Task BrowserRejectsPageOutsideConfiguredRoot()
    {
        var service = CreateService(request => Json("""
            {"id":"999","title":"Private","ancestors":[{"id":"888","title":"Other"}],"body":{"view":{"value":"Secret"}}}
            """));

        var page = await service.GetPageAsync("100", "999", "reader@example.com", false);

        page.Succeeded.Should().BeFalse();
        page.Message.Should().Contain("outside the configured");
    }

    [Fact]
    public void RendererPreservesPageStructureAndProxiesOnlySameOriginImages()
    {
        var renderer = new ConfluenceContentRenderer(new EphemeralDataProtectionProvider());
        const string html = """
            <h2 onclick="alert(1)">Release</h2>
            <table><tr><th colspan="2">Status</th></tr><tr><td>Ready</td><td><a href="/pages/viewpage.action?pageId=101">Details</a></td></tr></table>
            <a href="/display/SPACE/Build+Guide">Build guide</a>
            <img src="/download/attachments/101/build.png" alt="Build image" onerror="alert(1)" />
            <img src="https://external.example.test/tracker.png" alt="External" />
            <script>alert(1)</script>
            """;

        var tabId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var rendered = renderer.Render(
            html,
            "https://confluence.example.test",
            tabId,
            new Dictionary<string, string>
            {
                ["https://confluence.example.test/display/SPACE/Build+Guide"] = "102"
            });

        rendered.Should().Contain("<table>")
            .And.Contain("colspan=\"2\"")
            .And.Contain("href=\"/Confluence?tabId=11111111-1111-1111-1111-111111111111&amp;pageId=101\"")
            .And.Contain("href=\"/Confluence?tabId=11111111-1111-1111-1111-111111111111&amp;pageId=102\"")
            .And.Contain("src=\"/Confluence?handler=Image&amp;token=")
            .And.NotContain("onclick")
            .And.NotContain("onerror")
            .And.NotContain("external.example.test")
            .And.NotContain("<script");
    }

    private static ConfluenceBrowserService CreateService(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(new HttpClient(new StubHandler(response)), new StubConfiguration(), new StubCredentials());

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class StubCredentials : IAtlassianCredentialAccessor
    {
        public Task<AtlassianResolvedCredential?> ResolveConfluenceCredentialAsync(string userId, bool allowPrivilegedDefault, CancellationToken cancellationToken = default) =>
            Task.FromResult<AtlassianResolvedCredential?>(new("token", false, false));
        public Task<AtlassianResolvedCredential?> ResolveJiraCredentialAsync(string userId, bool allowPrivilegedDefault, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AtlassianResolvedCredential?> ResolveDefaultJiraCredentialAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubConfiguration : IAtlassianConfigurationService
    {
        public Task<AtlassianIntegrationSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AtlassianIntegrationSettings
            {
                ConfluenceEnabled = true,
                ConfluenceBaseUrl = "https://confluence.example.test",
                ConfluenceContentApiPath = "/rest/api/content",
                ConfluenceParentPageId = "100"
            });
        public Task<IReadOnlyList<ConfluenceBrowserTab>> ListConfluenceBrowserTabsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ConfluenceBrowserTab>>([]);
        public Task SaveConfluenceBrowserTabsAsync(IReadOnlyCollection<ConfluenceBrowserTab> tabs, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveSettingsAsync(AtlassianIntegrationSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StudioAtlassianMapping>> ListStudioMappingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StudioAtlassianMapping?> GetStudioMappingAsync(string studioId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveStudioMappingsAsync(IReadOnlyCollection<StudioAtlassianMapping> mappings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AtlassianConnectionStatus> GetConnectionStatusAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveUserTokensAsync(string userId, string? jiraToken, string? confluenceToken, bool removeJiraToken = false, bool removeConfluenceToken = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AtlassianDefaultCredentialStatus> GetDefaultCredentialStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveDefaultTokensAsync(string? jiraToken, string? confluenceToken, bool removeJiraToken = false, bool removeConfluenceToken = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AtlassianPrivilegedAccess>> ListPrivilegedAccessAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SavePrivilegedAccessAsync(IReadOnlyCollection<AtlassianPrivilegedAccess> access, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MoveUserTokensAsync(string currentUserId, string newUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteUserTokensAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
