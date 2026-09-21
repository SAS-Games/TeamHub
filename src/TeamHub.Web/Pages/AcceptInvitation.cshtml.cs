using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Authentication;

namespace TeamHub.Web.Pages;

[AllowAnonymous]
public sealed class AcceptInvitationModel(IUserAccessService users) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public string ConfirmPassword { get; set; } = string.Empty;

    public bool IsValid { get; private set; }
    public bool Success { get; private set; }
    public string? DisplayName { get; private set; }
    public string? Message { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        await LoadValidationAsync(cancellationToken);

    public async Task OnPostAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(Password, ConfirmPassword, StringComparison.Ordinal))
        {
            await LoadValidationAsync(cancellationToken);
            if (IsValid) Message = "Passwords do not match.";
            return;
        }
        var result = await users.SetPasswordWithTokenAsync(
            Token, AccountTokenPurposes.PrivilegedInvitation, Password, cancellationToken);
        Success = result.Success;
        Message = result.Message;
        if (!Success) await LoadValidationAsync(cancellationToken);
    }

    private async Task LoadValidationAsync(CancellationToken cancellationToken)
    {
        var validation = await users.ValidateAccountTokenAsync(
            Token, AccountTokenPurposes.PrivilegedInvitation, cancellationToken);
        IsValid = validation.IsValid;
        DisplayName = validation.DisplayName;
        Message = validation.IsValid ? null : validation.Message;
    }
}
