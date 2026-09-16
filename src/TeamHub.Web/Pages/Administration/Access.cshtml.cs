using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Authentication;
using TeamHub.Team;
using TeamHub.Web.AccessControl;

namespace TeamHub.Web.Pages.Administration;

[Authorize(Roles = TeamHubUserTypes.Admin)]
public sealed class AccessModel(
    IUserAccessService users,
    ITeamHubModuleCatalog moduleCatalog,
    ICustomTeamTabService customTabs) : PageModel
{
    public IReadOnlyList<ModuleOption> Modules { get; private set; } = [];
    public IReadOnlyList<string> UserTypes { get; } = TeamHubUserTypes.All;
    public IReadOnlyList<AccessLevel> AccessLevels { get; } = Enum.GetValues<AccessLevel>();
    public IReadOnlyList<AuthorizedUserRecord> AuthorizedUsers { get; private set; } = [];
    public IReadOnlyDictionary<string, AccessLevel> Permissions { get; private set; } =
        new Dictionary<string, AccessLevel>();
    public IReadOnlyDictionary<string, AccessLevel?> UserPermissions { get; private set; } =
        new Dictionary<string, AccessLevel?>();

    [BindProperty]
    public List<PermissionInput> Input { get; set; } = [];

    [BindProperty]
    public List<UserPermissionInput> UserInput { get; set; } = [];

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

    public async Task<IActionResult> OnPostUserOverridesAsync(CancellationToken cancellationToken)
    {
        await users.SetUserPermissionsAsync(UserInput.Select(item =>
            new UserModulePermissionRecord(item.AuthorizedUserId, item.Module, item.AccessLevel)).ToList(), cancellationToken);
        StatusMessage = "Per-user custom tab access saved.";
        return RedirectToPage();
    }

    public AccessLevel GetLevel(string module, string userType) =>
        Permissions.GetValueOrDefault(Key(module, userType), AccessLevel.NoAccess);

    public IReadOnlyList<AccessLevel> GetAccessLevels(string module) =>
        module.StartsWith(CustomTeamTabAccess.ModulePrefix, StringComparison.Ordinal)
            ? [AccessLevel.NoAccess, AccessLevel.ReadOnly, AccessLevel.Edit]
            : AccessLevels;

    public AccessLevel? GetUserLevel(string module, Guid authorizedUserId) =>
        UserPermissions.GetValueOrDefault(UserKey(module, authorizedUserId));

    public IReadOnlyList<ModuleOption> CustomTabModules =>
        Modules.Where(module => module.Key.StartsWith(CustomTeamTabAccess.ModulePrefix, StringComparison.Ordinal)).ToList();

    public bool IsLocked(string module, string userType) =>
        userType == TeamHubUserTypes.Admin
        || module is TeamHubModules.UserManagement or TeamHubModules.AccessManagement;

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var tabs = await customTabs.ListTabsAsync(cancellationToken);
        var dynamicModules = tabs
            .Select(tab => new ModuleOption(CustomTeamTabAccess.ModuleForSlug(tab.Slug), $"Team - {tab.Name}"))
            .ToList();
        await users.EnsureModulesAsync(dynamicModules.Select(item => item.Key).ToList(), cancellationToken);

        Modules = moduleCatalog.Modules
            .Where(module => !module.StartsWith(CustomTeamTabAccess.ModulePrefix, StringComparison.Ordinal))
            .Select(module => new ModuleOption(module, module))
            .Concat(dynamicModules)
            .ToList();
        Permissions = (await users.ListPermissionsAsync(cancellationToken))
            .ToDictionary(item => Key(item.Module, item.UserType), item => item.AccessLevel);
        AuthorizedUsers = (await users.ListUsersAsync(cancellationToken))
            .Where(user => user.IsActive && user.UserType != TeamHubUserTypes.Admin)
            .ToList();
        UserPermissions = (await users.ListUserPermissionsAsync(cancellationToken))
            .ToDictionary(
                item => UserKey(item.Module, item.AuthorizedUserId),
                item => item.AccessLevel);
    }

    private static string Key(string module, string userType) => module + "|" + userType;
    private static string UserKey(string module, Guid authorizedUserId) => module + "|" + authorizedUserId;

    public sealed record ModuleOption(string Key, string Label);

    public sealed class PermissionInput
    {
        public string Module { get; set; } = string.Empty;
        public string UserType { get; set; } = string.Empty;
        public AccessLevel AccessLevel { get; set; }
    }

    public sealed class UserPermissionInput
    {
        public Guid AuthorizedUserId { get; set; }
        public string Module { get; set; } = string.Empty;
        public AccessLevel? AccessLevel { get; set; }
    }
}
