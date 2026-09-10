using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Authentication;

namespace TeamHub.Web.Pages;

[AllowAnonymous]
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
            Message = "Passwords do not match.";
            return;
        }

        var result = await users.RegisterAsync(
            new RegisterAuthorizedUserRequest(Input.UserId, Input.Password), cancellationToken);
        Success = result.Success;
        Message = result.Message;
    }

    public sealed class RegisterInput
    {
        public string UserId { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
