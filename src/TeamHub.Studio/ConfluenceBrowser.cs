using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TeamHub.Studio;

public sealed record ConfluenceBrowserPage(
    string Id,
    string Title,
    string ParentId,
    int Depth,
    string PageUrl);

public sealed record ConfluenceBrowserBreadcrumb(string Id, string Title);

public sealed class ConfluenceBrowserNavigationResult
{
    public bool IsConfigured { get; set; }
    public bool IsUsingSharedCredential { get; set; }
    public string RootPageId { get; set; } = string.Empty;
    public string? Message { get; set; }
    public IReadOnlyList<ConfluenceBrowserPage> Pages { get; set; } = [];
}

public sealed class ConfluenceBrowserContentResult
{
    public bool Succeeded { get; set; }
    public bool IsUsingSharedCredential { get; set; }
    public string PageId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string PageUrl { get; set; } = string.Empty;
    public string RenderedHtml { get; set; } = string.Empty;
    public string? Message { get; set; }
    public IReadOnlyList<ConfluenceBrowserBreadcrumb> Breadcrumbs { get; set; } = [];
}

public sealed record ConfluenceBrowserAsset(byte[] Content, string ContentType);

public interface IConfluenceBrowserService
{
    Task<ConfluenceBrowserNavigationResult> GetNavigationAsync(
        string rootPageId,
        string requestingUserId,
        bool allowPrivilegedDefaultCredential,
        CancellationToken cancellationToken = default);

    Task<ConfluenceBrowserContentResult> GetPageAsync(
        string rootPageId,
        string pageId,
        string requestingUserId,
        bool allowPrivilegedDefaultCredential,
        CancellationToken cancellationToken = default);

    Task<ConfluenceBrowserAsset?> GetImageAsync(
        string sourceUrl,
        string requestingUserId,
        bool allowPrivilegedDefaultCredential,
        CancellationToken cancellationToken = default);
}

internal sealed class ConfluenceBrowserService(
    HttpClient httpClient,
    IAtlassianConfigurationService configuration,
    IAtlassianCredentialAccessor credentials) : IConfluenceBrowserService
{
    private const int PageLimit = 200;
    private const int MaximumPages = 2_000;
    private const int MaximumImageBytes = 15 * 1024 * 1024;

    public async Task<ConfluenceBrowserNavigationResult> GetNavigationAsync(
        string rootPageId,
        string requestingUserId,
        bool allowPrivilegedDefaultCredential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPageId) || rootPageId.Length > 256)
            return new ConfluenceBrowserNavigationResult { Message = "The selected Confluence tab has an invalid root page ID." };
        rootPageId = rootPageId.Trim();
        var context = await ResolveAsync(requestingUserId, allowPrivilegedDefaultCredential, cancellationToken);
        if (context.Message is not null) return new ConfluenceBrowserNavigationResult { Message = context.Message };
        var settings = context.Settings!;

        var root = await FetchPageAsync(context, rootPageId, includeBody: false, cancellationToken);
        if (root is null)
        {
            return new ConfluenceBrowserNavigationResult
            {
                IsConfigured = true,
                IsUsingSharedCredential = context.Credential!.IsShared,
                RootPageId = rootPageId,
                Message = "The configured Confluence root page is unavailable to this connection."
            };
        }

        var pages = new List<ConfluenceBrowserPage>
        {
            new(root.Id, root.Title, string.Empty, 0, BuildPageUrl(settings.ConfluenceBaseUrl, root.Id, root.WebUiPath))
        };
        for (var start = 0; pages.Count < MaximumPages; start += PageLimit)
        {
            var requestUri = $"{BuildContentItemUri(settings, root.Id)}/descendant/page?limit={PageLimit}&start={start}&expand=ancestors";
            using var document = await GetJsonAsync(context, requestUri, cancellationToken);
            if (document is null || !document.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array) break;

            var count = 0;
            foreach (var item in results.EnumerateArray())
            {
                count++;
                var page = ParsePage(item);
                if (string.IsNullOrWhiteSpace(page.Id) || !TryGetPositionBelowRoot(item, root.Id, out var parentId, out var depth)) continue;
                pages.Add(new ConfluenceBrowserPage(
                    page.Id,
                    page.Title,
                    parentId,
                    depth,
                    BuildPageUrl(settings.ConfluenceBaseUrl, page.Id, page.WebUiPath)));
                if (pages.Count >= MaximumPages) break;
            }
            if (count < PageLimit) break;
        }

        return new ConfluenceBrowserNavigationResult
        {
            IsConfigured = true,
            IsUsingSharedCredential = context.Credential!.IsShared,
            RootPageId = root.Id,
            Pages = pages
        };
    }

    public async Task<ConfluenceBrowserContentResult> GetPageAsync(
        string rootPageId,
        string pageId,
        string requestingUserId,
        bool allowPrivilegedDefaultCredential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPageId) || rootPageId.Length > 256
            || string.IsNullOrWhiteSpace(pageId) || pageId.Length > 128)
            return new ConfluenceBrowserContentResult { Message = "Select a valid Confluence page." };
        rootPageId = rootPageId.Trim();

        var context = await ResolveAsync(requestingUserId, allowPrivilegedDefaultCredential, cancellationToken);
        if (context.Message is not null) return new ConfluenceBrowserContentResult { Message = context.Message };
        var settings = context.Settings!;
        var page = await FetchPageAsync(context, pageId.Trim(), includeBody: true, cancellationToken);
        if (page is null)
            return Failure(context, "The selected Confluence page was not found or is not available to this connection.");

        var rootId = rootPageId;
        if (!string.Equals(page.Id, rootId, StringComparison.Ordinal)
            && !page.Ancestors.Any(item => string.Equals(item.Id, rootId, StringComparison.Ordinal)))
        {
            return Failure(context, "The selected page is outside the configured Confluence root.");
        }

        var rootIndex = page.Ancestors.FindIndex(item => string.Equals(item.Id, rootId, StringComparison.Ordinal));
        var breadcrumbs = (rootIndex < 0 ? [] : page.Ancestors.Skip(rootIndex))
            .Append(new ConfluenceBrowserBreadcrumb(page.Id, page.Title))
            .ToList();
        return new ConfluenceBrowserContentResult
        {
            Succeeded = true,
            IsUsingSharedCredential = context.Credential!.IsShared,
            PageId = page.Id,
            Title = page.Title,
            PageUrl = BuildPageUrl(settings.ConfluenceBaseUrl, page.Id, page.WebUiPath),
            RenderedHtml = page.RenderedHtml,
            Breadcrumbs = breadcrumbs
        };
    }

    public async Task<ConfluenceBrowserAsset?> GetImageAsync(
        string sourceUrl,
        string requestingUserId,
        bool allowPrivilegedDefaultCredential,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(requestingUserId, allowPrivilegedDefaultCredential, cancellationToken);
        if (context.Message is not null || !TryResolveConfluenceUri(context.Settings!.ConfluenceBaseUrl, sourceUrl, out var uri)) return null;

        using var request = CreateRequest(uri.AbsoluteUri, context.Credential!.Token, acceptJson: false);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(contentType) || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
        if (response.Content.Headers.ContentLength > MaximumImageBytes) return null;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > MaximumImageBytes) return null;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return new ConfluenceBrowserAsset(output.ToArray(), contentType);
    }

    private async Task<BrowserContext> ResolveAsync(
        string requestingUserId,
        bool allowPrivilegedDefaultCredential,
        CancellationToken cancellationToken)
    {
        var settings = await configuration.GetSettingsAsync(cancellationToken);
        if (!settings.ConfluenceEnabled) return new(settings, null, "Confluence integration is disabled.");
        if (string.IsNullOrWhiteSpace(settings.ConfluenceBaseUrl)) return new(settings, null, "The Confluence base URL is not configured.");
        if (string.IsNullOrWhiteSpace(requestingUserId)) return new(settings, null, "Sign in to browse Confluence pages.");

        try
        {
            var credential = await credentials.ResolveConfluenceCredentialAsync(
                requestingUserId,
                allowPrivilegedDefaultCredential,
                cancellationToken);
            return credential is null
                ? new(settings, null, "Connect your Confluence token from My Connections to browse pages.")
                : new(settings, credential, null);
        }
        catch (InvalidOperationException exception)
        {
            return new(settings, null, exception.Message);
        }
    }

    private async Task<PageData?> FetchPageAsync(
        BrowserContext context,
        string pageId,
        bool includeBody,
        CancellationToken cancellationToken)
    {
        var expand = includeBody ? "body.view,ancestors,version" : "ancestors,version";
        var uri = $"{BuildContentItemUri(context.Settings!, pageId)}?expand={Uri.EscapeDataString(expand)}";
        using var document = await GetJsonAsync(context, uri, cancellationToken);
        return document is null ? null : ParsePage(document.RootElement);
    }

    private async Task<JsonDocument?> GetJsonAsync(BrowserContext context, string uri, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(uri, context.Credential!.Token);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HttpRequestMessage CreateRequest(string uri, string token, bool acceptJson = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (acceptJson) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Atlassian-Token", "no-check");
        return request;
    }

    private static PageData ParsePage(JsonElement item)
    {
        var ancestors = new List<ConfluenceBrowserBreadcrumb>();
        if (item.TryGetProperty("ancestors", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            ancestors.AddRange(values.EnumerateArray().Select(value =>
                new ConfluenceBrowserBreadcrumb(GetString(value, "id"), GetString(value, "title"))));
        }
        var renderedHtml = item.TryGetProperty("body", out var body)
            && body.TryGetProperty("view", out var view)
            ? GetString(view, "value")
            : string.Empty;
        var webUiPath = item.TryGetProperty("_links", out var links) ? GetString(links, "webui") : string.Empty;
        return new PageData(GetString(item, "id"), GetString(item, "title"), webUiPath, renderedHtml, ancestors);
    }

    private static bool TryGetPositionBelowRoot(JsonElement item, string rootId, out string parentId, out int depth)
    {
        parentId = string.Empty;
        depth = 0;
        if (!item.TryGetProperty("ancestors", out var ancestors) || ancestors.ValueKind != JsonValueKind.Array) return false;
        var values = ancestors.EnumerateArray().ToList();
        var rootIndex = values.FindIndex(value => string.Equals(GetString(value, "id"), rootId, StringComparison.Ordinal));
        if (rootIndex < 0) return false;
        parentId = values.Count == 0 ? rootId : GetString(values[^1], "id");
        depth = values.Count - rootIndex;
        return true;
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string BuildContentItemUri(AtlassianIntegrationSettings settings, string pageId) =>
        $"{settings.ConfluenceBaseUrl.TrimEnd('/')}/{settings.ConfluenceContentApiPath.Trim('/')}/{Uri.EscapeDataString(pageId)}";

    private static string BuildPageUrl(string baseUrl, string pageId, string webUiPath)
    {
        if (Uri.TryCreate(webUiPath, UriKind.Absolute, out var absolute)
            && absolute.Scheme is "http" or "https") return absolute.AbsoluteUri;
        if (!string.IsNullOrWhiteSpace(webUiPath))
            return $"{baseUrl.TrimEnd('/')}/{webUiPath.TrimStart('/')}";
        return $"{baseUrl.TrimEnd('/')}/pages/viewpage.action?pageId={Uri.EscapeDataString(pageId)}";
    }

    private static bool TryResolveConfluenceUri(string baseUrl, string source, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)) return false;
        if (!Uri.TryCreate(baseUri, WebUtility.HtmlDecode(source), out var candidate)) return false;
        if (candidate.Scheme is not ("http" or "https")
            || !string.Equals(candidate.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(candidate.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
            || candidate.Port != baseUri.Port
            || !baseUri.IsBaseOf(candidate)) return false;
        uri = candidate;
        return true;
    }

    private static ConfluenceBrowserContentResult Failure(BrowserContext context, string message) => new()
    {
        IsUsingSharedCredential = context.Credential?.IsShared == true,
        Message = message
    };

    private sealed record BrowserContext(
        AtlassianIntegrationSettings? Settings,
        AtlassianResolvedCredential? Credential,
        string? Message);

    private sealed record PageData(
        string Id,
        string Title,
        string WebUiPath,
        string RenderedHtml,
        List<ConfluenceBrowserBreadcrumb> Ancestors);
}
