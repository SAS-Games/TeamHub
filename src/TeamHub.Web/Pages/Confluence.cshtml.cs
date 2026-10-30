using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Studio;
using TeamHub.Web.Confluence;

namespace TeamHub.Web.Pages;

public sealed class ConfluenceModel(
    IConfluenceBrowserService browser,
    IAtlassianConfigurationService configuration,
    IConfluenceContentRenderer renderer) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? PageId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? TabId { get; set; }

    public IReadOnlyList<ConfluenceBrowserTab> Tabs { get; private set; } = [];
    public ConfluenceBrowserTab? SelectedTab { get; private set; }
    public ConfluenceBrowserNavigationResult Navigation { get; private set; } = new();
    public ConfluenceBrowserContentResult PageContent { get; private set; } = new();
    public IReadOnlyList<ConfluenceBrowserPage> OrderedPages { get; private set; } = [];
    public string RenderedHtml { get; private set; } = string.Empty;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var userId = User.Identity?.Name ?? string.Empty;
        var allowShared = User.IsInRole("Privileged");
        try
        {
            var settings = await configuration.GetSettingsAsync(cancellationToken);
            Tabs = (await configuration.ListConfluenceBrowserTabsAsync(cancellationToken))
                .Where(tab => tab.IsEnabled)
                .OrderBy(tab => tab.DisplayOrder)
                .ThenBy(tab => tab.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (Tabs.Count == 0 && !string.IsNullOrWhiteSpace(settings.ConfluenceParentPageId))
            {
                Tabs = [new ConfluenceBrowserTab(Guid.Empty, "Pages", settings.ConfluenceParentPageId, 0, true)];
            }
            SelectedTab = Tabs.FirstOrDefault(tab => tab.Id == TabId) ?? Tabs.FirstOrDefault();
            if (SelectedTab is null)
            {
                PageContent = new ConfluenceBrowserContentResult
                {
                    Message = "No Confluence tabs are configured. Ask an administrator to add one under Jira & Confluence Configuration."
                };
                return;
            }
            TabId = SelectedTab.Id;
            Navigation = await browser.GetNavigationAsync(SelectedTab.RootPageId, userId, allowShared, cancellationToken);
            OrderedPages = OrderPages(Navigation.Pages, Navigation.RootPageId);
            var selectedPageId = string.IsNullOrWhiteSpace(PageId) ? Navigation.RootPageId : PageId.Trim();
            if (string.IsNullOrWhiteSpace(selectedPageId)) return;

            PageContent = await browser.GetPageAsync(SelectedTab.RootPageId, selectedPageId, userId, allowShared, cancellationToken);
            if (!PageContent.Succeeded && !string.IsNullOrWhiteSpace(PageId))
            {
                foreach (var alternative in Tabs.Where(tab => tab.Id != SelectedTab.Id))
                {
                    var candidate = await browser.GetPageAsync(alternative.RootPageId, selectedPageId, userId, allowShared, cancellationToken);
                    if (!candidate.Succeeded) continue;
                    SelectedTab = alternative;
                    TabId = alternative.Id;
                    PageContent = candidate;
                    Navigation = await browser.GetNavigationAsync(alternative.RootPageId, userId, allowShared, cancellationToken);
                    OrderedPages = OrderPages(Navigation.Pages, Navigation.RootPageId);
                    break;
                }
            }
            if (!PageContent.Succeeded) return;
            var pageIdsByUrl = Navigation.Pages
                .Where(page => !string.IsNullOrWhiteSpace(page.PageUrl))
                .GroupBy(page => page.PageUrl, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);
            RenderedHtml = renderer.Render(
                PageContent.RenderedHtml,
                settings.ConfluenceBaseUrl,
                SelectedTab.Id,
                pageIdsByUrl);
            PageId = PageContent.PageId;
        }
        catch (HttpRequestException exception)
        {
            PageContent = new ConfluenceBrowserContentResult
            {
                Message = $"Confluence could not be reached: {exception.Message}"
            };
        }
    }

    public async Task<IActionResult> OnGetImageAsync(string? token, CancellationToken cancellationToken)
    {
        if (!renderer.TryReadImageToken(token ?? string.Empty, out var sourceUrl)) return NotFound();
        var image = await browser.GetImageAsync(
            sourceUrl,
            User.Identity?.Name ?? string.Empty,
            User.IsInRole("Privileged"),
            cancellationToken);
        if (image is null) return NotFound();
        Response.Headers.CacheControl = "private, max-age=300";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(image.Content, image.ContentType);
    }

    private static IReadOnlyList<ConfluenceBrowserPage> OrderPages(
        IReadOnlyList<ConfluenceBrowserPage> pages,
        string rootPageId)
    {
        if (pages.Count == 0) return [];
        var result = new List<ConfluenceBrowserPage>(pages.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var children = pages
            .GroupBy(page => page.ParentId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(page => page.Title, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.Ordinal);

        void Add(string id)
        {
            var page = pages.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (page is null || !visited.Add(page.Id)) return;
            result.Add(page);
            if (!children.TryGetValue(page.Id, out var nested)) return;
            foreach (var child in nested) Add(child.Id);
        }

        Add(rootPageId);
        foreach (var page in pages.OrderBy(page => page.Depth).ThenBy(page => page.Title, StringComparer.OrdinalIgnoreCase)) Add(page.Id);
        return result;
    }
}
