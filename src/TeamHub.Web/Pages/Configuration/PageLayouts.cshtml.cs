using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Team;
using TeamHub.Web.Layout;

namespace TeamHub.Web.Pages.Configuration;

[Authorize(Roles = "Admin")]
public sealed class PageLayoutsModel(IPageLayoutService pageLayouts) : PageModel
{
    [BindProperty]
    public List<PageLayoutInput> Pages { get; set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        await LoadAsync(null, cancellationToken);

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        ModelState.Clear();
        var submitted = Pages
            .Where(page => !string.IsNullOrWhiteSpace(page.PageKey))
            .GroupBy(page => page.PageKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().PageWidth, StringComparer.OrdinalIgnoreCase);
        var settings = new List<PageLayoutSettingDto>();

        foreach (var definition in PageLayoutCatalog.Pages)
        {
            var requestedWidth = submitted.GetValueOrDefault(definition.PageKey) ?? definition.DefaultWidth;
            try
            {
                settings.Add(new PageLayoutSettingDto
                {
                    PageKey = definition.PageKey,
                    PageWidth = CustomTeamPageWidths.Normalize(requestedWidth)
                });
            }
            catch (ArgumentException)
            {
                ModelState.AddModelError(string.Empty, $"Select a supported width for {definition.Name}.");
            }
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(submitted, cancellationToken);
            return Page();
        }

        await pageLayouts.SavePageWidthsAsync(settings, cancellationToken);
        StatusMessage = "Page layout settings saved.";
        return RedirectToPage();
    }

    private async Task LoadAsync(
        IReadOnlyDictionary<string, string>? submitted,
        CancellationToken cancellationToken)
    {
        var saved = (await pageLayouts.GetPageWidthsAsync(cancellationToken))
            .ToDictionary(setting => setting.PageKey, setting => setting.PageWidth, StringComparer.OrdinalIgnoreCase);

        Pages = PageLayoutCatalog.Pages.Select(definition => new PageLayoutInput
        {
            PageKey = definition.PageKey,
            Name = definition.Name,
            Category = definition.Category,
            PageWidth = submitted?.GetValueOrDefault(definition.PageKey)
                ?? saved.GetValueOrDefault(definition.PageKey)
                ?? definition.DefaultWidth
        }).ToList();
    }

    public sealed class PageLayoutInput
    {
        public string PageKey { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string PageWidth { get; set; } = CustomTeamPageWidths.Standard;
    }
}
