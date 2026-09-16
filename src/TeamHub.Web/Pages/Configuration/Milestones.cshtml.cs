using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Milestones;

namespace TeamHub.Web.Pages.Configuration;

[Authorize(Roles = "Admin")]
public sealed class MilestonesModel(IMilestoneConfigurationService configurationService) : PageModel
{
    [BindProperty]
    public MilestoneSourceSettings Settings { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Settings = await configurationService.GetSettingsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await configurationService.SaveSettingsAsync(Settings, cancellationToken);
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }

        StatusMessage = "Milestone workbook configuration saved.";
        return RedirectToPage();
    }
}
