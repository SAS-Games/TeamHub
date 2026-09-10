using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Authentication;
using TeamHub.Web.AccessControl;

namespace TeamHub.Web.Pages.Administration;

[Authorize(Roles = TeamHubUserTypes.Admin)]
public sealed class AccessModel(IUserAccessService users, ITeamHubModuleCatalog moduleCatalog) : PageModel
{
    public IReadOnlyList<string> Modules => moduleCatalog.Modules;
    public IReadOnlyList<string> UserTypes { get; } = TeamHubUserTypes.All;
    public IReadOnlyList<AccessLevel> AccessLevels { get; } = Enum.GetValues<AccessLevel>();
    public IReadOnlyDictionary<string, AccessLevel> Permissions { get; private set; } =
        new Dictionary<string, AccessLevel>();

    [BindProperty]
    public List<PermissionInput> Input { get; set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await users.SetPermissionsAsync(Input.Select(item =>
            new ModulePermissionRecord(item.Module, item.UserType, item.AccessLevel)).ToList(), cancellationToken);
        StatusMessage = "Access matrix saved.";
        return RedirectToPage();
    }

    public AccessLevel GetLevel(string module, string userType) =>
        Permissions.GetValueOrDefault(Key(module, userType), AccessLevel.NoAccess);

    public bool IsLocked(string module, string userType) =>
        userType == TeamHubUserTypes.Admin
        || module is TeamHubModules.UserManagement or TeamHubModules.AccessManagement;

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Permissions = (await users.ListPermissionsAsync(cancellationToken))
            .ToDictionary(item => Key(item.Module, item.UserType), item => item.AccessLevel);
    }

    private static string Key(string module, string userType) => module + "|" + userType;

    public sealed class PermissionInput
    {
        public string Module { get; set; } = string.Empty;
        public string UserType { get; set; } = string.Empty;
        public AccessLevel AccessLevel { get; set; }
    }
}
