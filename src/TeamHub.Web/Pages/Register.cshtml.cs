using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Authentication;

namespace TeamHub.Web.Pages;

[AllowAnonymous]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class RegisterModel(IUserAccessService users) : PageModel
{
    [BindProperty]
    public RegisterInput Input { get; set; } = new();

    public string? Message { get; private set; }
    public bool Success { get; private set; }

    public async Task OnPostAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(Input.Password, Input.ConfirmPassword, StringComparison.Ordinal))
        {
            ClearPasswords();
            Message = "Passwords do not match.";
            return;
        }

        var result = await users.RegisterAsync(
            new RegisterAuthorizedUserRequest(Input.Identifier, Input.Password), cancellationToken);
        Success = result.Success;
        Message = result.Message;
        ClearPasswords();
    }

    private void ClearPasswords()
    {
        Input.Password = string.Empty;
        Input.ConfirmPassword = string.Empty;
        ModelState.Remove("Input.Password");
        ModelState.Remove("Input.ConfirmPassword");
    }

    public sealed class RegisterInput
    {
        public string Identifier { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
