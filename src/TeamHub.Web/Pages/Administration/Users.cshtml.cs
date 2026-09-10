using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Authentication;

namespace TeamHub.Web.Pages.Administration;

[Authorize(Roles = TeamHubUserTypes.Admin)]
public sealed class UsersModel(IUserAccessService users) : PageModel
{
    public IReadOnlyList<AuthorizedUserRecord> AuthorizedUsers { get; private set; } = [];
    public IReadOnlyList<string> UserTypes { get; } =
        [TeamHubUserTypes.Registered, TeamHubUserTypes.Privileged, TeamHubUserTypes.Admin];

    [BindProperty]
    public UserInput Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(Guid? editId, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        if (!editId.HasValue) return;
        var user = await users.GetUserAsync(editId.Value, cancellationToken);
        if (user is null) return;
        Input = new UserInput
        {
            Id = user.Id,
            UserId = user.UserId,
            DisplayName = user.DisplayName,
            UserType = user.UserType,
            IsActive = user.IsActive
        };
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await users.SaveUserAsync(new SaveAuthorizedUserRequest(
                Input.Id,
                Input.UserId,
                Input.DisplayName,
                Input.UserType,
                Input.IsActive,
                Input.TemporaryPassword), User.Identity?.Name, cancellationToken);
            StatusMessage = Input.Id.HasValue ? "Authorized user updated." : "Authorized user added.";
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            ErrorMessage = exception.Message;
            await LoadAsync(cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid id, bool active, CancellationToken cancellationToken)
    {
        try
        {
            await users.SetUserActiveAsync(id, active, User.Identity?.Name, cancellationToken);
            StatusMessage = active ? "User activated." : "User deactivated.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            StatusMessage = exception.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await users.DeleteUserAsync(id, User.Identity?.Name, cancellationToken);
            StatusMessage = "Authorized user removed.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            StatusMessage = exception.Message;
        }
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken) =>
        AuthorizedUsers = await users.ListUsersAsync(cancellationToken);

    public sealed class UserInput
    {
        public Guid? Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string UserType { get; set; } = TeamHubUserTypes.Registered;
        public bool IsActive { get; set; } = true;
        public string? TemporaryPassword { get; set; }
    }
}
