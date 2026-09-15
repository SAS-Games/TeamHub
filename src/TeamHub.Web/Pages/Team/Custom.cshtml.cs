using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Authentication;
using TeamHub.Team;
using TeamHub.Web.AccessControl;

namespace TeamHub.Web.Pages.Team;

[Authorize]
public sealed class CustomModel(ICustomTeamTabService tabs, ICurrentAccessService access) : PageModel
{
    public CustomTeamTabDto Tab { get; private set; } = new();
    public bool CanEdit { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken cancellationToken)
    {
        var loaded = await tabs.GetTabAsync(slug, cancellationToken);
        if (loaded is null) return NotFound();
        Tab = loaded;
        CanEdit = await access.CanAsync(CustomTeamTabAccess.ModuleForSlug(Tab.Slug), AccessLevel.Edit, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveRowAsync(
        string slug,
        string tableId,
        string? rowId,
        int version,
        Dictionary<string, string?> values,
        CancellationToken cancellationToken)
    {
        if (!await CanEditAsync(slug, cancellationToken)) return Forbid();
        try
        {
            await tabs.SaveRowAsync(new SaveCustomTeamRowRequest(
                tableId, rowId, version, values, CurrentActor()), cancellationToken);
            StatusMessage = string.IsNullOrWhiteSpace(rowId) ? "Row added." : "Row updated.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            ErrorMessage = exception.Message;
        }
        return RedirectToPage(new { slug });
    }

    public async Task<IActionResult> OnPostRemoveRowAsync(
        string slug,
        string tableId,
        string rowId,
        int version,
        CancellationToken cancellationToken)
    {
        if (!await CanEditAsync(slug, cancellationToken)) return Forbid();
        try
        {
            await tabs.RemoveRowAsync(tableId, rowId, version, CurrentActor(), cancellationToken);
            StatusMessage = "Row removed.";
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
        }
        return RedirectToPage(new { slug });
    }

    public string Value(CustomTeamRowDto row, CustomTeamColumnDto column) =>
        row.Values.GetValueOrDefault(column.Key) ?? string.Empty;

    private async Task<bool> CanEditAsync(string slug, CancellationToken cancellationToken)
    {
        var tab = await tabs.GetTabAsync(slug, cancellationToken);
        return tab is not null
            && await access.CanAsync(CustomTeamTabAccess.ModuleForSlug(tab.Slug), AccessLevel.Edit, cancellationToken);
    }

    private string CurrentActor() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? "Unknown";
}
