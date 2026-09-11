using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Studio;

namespace TeamHub.Web.Pages.Profile;

[Authorize]
public sealed class AtlassianModel(IAtlassianConfigurationService configurationService) : PageModel
{
    [BindProperty]
    public TokenInput Input { get; set; } = new();

    public AtlassianConnectionStatus Status { get; private set; } = new(false, null, false, null);

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadStatusAsync(cancellationToken);

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var userId = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            await LoadStatusAsync(cancellationToken);
            return Page();
        }

        await configurationService.SaveUserTokensAsync(
            userId,
            Input.JiraToken,
            Input.ConfluenceToken,
            Input.RemoveJiraToken,
            Input.RemoveConfluenceToken,
            cancellationToken);

        TempData["StatusMessage"] = "Your Atlassian connection settings were saved securely.";
        return RedirectToPage();
    }

    private async Task LoadStatusAsync(CancellationToken cancellationToken)
    {
        var userId = User.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            Status = await configurationService.GetConnectionStatusAsync(userId, cancellationToken);
        }
    }

    public sealed class TokenInput
    {
        [DataType(DataType.Password)]
        [StringLength(4096)]
        public string? JiraToken { get; set; }

        [DataType(DataType.Password)]
        [StringLength(4096)]
        public string? ConfluenceToken { get; set; }

        public bool RemoveJiraToken { get; set; }
        public bool RemoveConfluenceToken { get; set; }
    }
}
