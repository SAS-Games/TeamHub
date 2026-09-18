using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Authentication;

namespace TeamHub.Web.Pages;

[AllowAnonymous]
public sealed class ResetPasswordModel(IUserAccessService users) : PageModel
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

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var validation = await users.ValidateAccountTokenAsync(
            Token, AccountTokenPurposes.PasswordReset, cancellationToken);
        IsValid = validation.IsValid;
        DisplayName = validation.DisplayName;
        Message = validation.IsValid ? null : validation.Message;
    }

    public async Task OnPostAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(Password, ConfirmPassword, StringComparison.Ordinal))
        {
            var validation = await users.ValidateAccountTokenAsync(
                Token, AccountTokenPurposes.PasswordReset, cancellationToken);
            IsValid = validation.IsValid;
            DisplayName = validation.DisplayName;
            Message = validation.IsValid ? "Passwords do not match." : validation.Message;
            return;
        }
        var result = await users.SetPasswordWithTokenAsync(
            Token, AccountTokenPurposes.PasswordReset, Password, cancellationToken);
        Success = result.Success;
        IsValid = !result.Success && (await users.ValidateAccountTokenAsync(
            Token, AccountTokenPurposes.PasswordReset, cancellationToken)).IsValid;
        Message = result.Message;
    }
}
