namespace TeamHub.Authentication;

public static class TeamHubUserTypes
{
    public const string Guest = "Guest";
    public const string Registered = "Registered";
    public const string Privileged = "Privileged";
    public const string Admin = "Admin";

    public static IReadOnlyList<string> All { get; } = [Guest, Registered, Privileged, Admin];

    public static string Normalize(string? value) => All.FirstOrDefault(
        item => string.Equals(item, value?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("Select a valid user type.", nameof(value));
}

public static class TeamHubModules
{
    public const string Home = "Home";
    public const string UsefulLinks = "Useful Links";
    public const string Team = "Team";
    public const string WorkCenter = "Work Center";
    public const string Milestones = "Milestones";
    public const string MilestoneReview = "Milestone Review";
    public const string FlowDesigner = "Flow Designer";
    public const string StudioConfiguration = "Studio Configuration";
    public const string SupportSummary = "Support Summary";
    public const string AiReports = "AI Reports";
    public const string AtlassianConnection = "Atlassian Connection";
    public const string Notifications = "Notifications";
    public const string Administration = "Administration";
    public const string UserManagement = "User Management";
    public const string AccessManagement = "Access Management";

    public static IReadOnlyList<string> All { get; } =
    [
        Home,
        UsefulLinks,
        Team,
        WorkCenter,
        Milestones,
        MilestoneReview,
        FlowDesigner,
        StudioConfiguration,
        SupportSummary,
        AiReports,
        AtlassianConnection,
        Notifications,
        Administration,
        UserManagement,
        AccessManagement
    ];
}

public static class TeamHubModuleAccess
{
    private static readonly IReadOnlyList<AccessLevel> ReadOnly =
        [AccessLevel.NoAccess, AccessLevel.ReadOnly];
    private static readonly IReadOnlyList<AccessLevel> Editable =
        [AccessLevel.NoAccess, AccessLevel.ReadOnly, AccessLevel.Edit];
    private static readonly IReadOnlyList<AccessLevel> Creatable =
        [AccessLevel.NoAccess, AccessLevel.Create];
    private static readonly IReadOnlyList<AccessLevel> WorkItems =
        [AccessLevel.NoAccess, AccessLevel.ReadOnly, AccessLevel.Create, AccessLevel.Edit];
    private static readonly IReadOnlyList<AccessLevel> Crud =
        [AccessLevel.NoAccess, AccessLevel.ReadOnly, AccessLevel.Create, AccessLevel.Edit, AccessLevel.Delete];
    private static readonly IReadOnlyList<AccessLevel> FlowDesigner =
        [AccessLevel.NoAccess, AccessLevel.ReadOnly, AccessLevel.Create, AccessLevel.Edit, AccessLevel.FullAccess];

    public static IReadOnlyList<AccessLevel> SupportedLevels(string module) => module switch
    {
        TeamHubModules.Home or TeamHubModules.UsefulLinks or TeamHubModules.Milestones
            or TeamHubModules.Notifications => ReadOnly,
        TeamHubModules.Team => ReadOnly,
        TeamHubModules.WorkCenter => WorkItems,
        TeamHubModules.StudioConfiguration => Crud,
        TeamHubModules.FlowDesigner => FlowDesigner,
        TeamHubModules.AiReports => Creatable,
        TeamHubModules.MilestoneReview or TeamHubModules.SupportSummary
            or TeamHubModules.AtlassianConnection => Editable,
        TeamHubModules.Administration or TeamHubModules.UserManagement
            or TeamHubModules.AccessManagement => [AccessLevel.NoAccess, AccessLevel.FullAccess],
        _ => Editable
    };

    public static AccessLevel Normalize(string module, AccessLevel requested)
    {
        var supported = SupportedLevels(module);
        if (supported.Contains(requested)) return requested;

        return supported
            .Where(level => level <= requested)
            .DefaultIfEmpty(AccessLevel.NoAccess)
            .Max();
    }

    public static string Description(string module) => module switch
    {
        TeamHubModules.Home => "View the Home page.",
        TeamHubModules.UsefulLinks => "Show useful links on the Home page.",
        TeamHubModules.Team => "View team pages. Team configuration remains Admin-only.",
        TeamHubModules.WorkCenter => "Read views workflows allowed for the user's role, Create starts them, and Edit completes assigned tasks. Workflow-library administration remains Admin-only.",
        TeamHubModules.Milestones => "View milestone information.",
        TeamHubModules.MilestoneReview => "Read views reviews and Jira feedback; Edit updates the user's feedback. Administrative restore and publishing remain Admin-only.",
        TeamHubModules.FlowDesigner => "Read views flows, Create adds flows, Edit changes owned flows, and Full Access manages shared flows and templates. Publication review remains Admin-only.",
        TeamHubModules.StudioConfiguration => "Read views studios, Create adds studios, Edit updates them, and Delete removes them.",
        TeamHubModules.SupportSummary => "Read views support information; Edit updates Confluence and support comments. Planning administration remains Admin-only.",
        TeamHubModules.AiReports => "Create generates AI reports; No Access disables report generation.",
        TeamHubModules.AtlassianConnection => "Read views connection status; Edit changes the user's connection.",
        TeamHubModules.Notifications => "Read views notifications and manages the user's read state.",
        _ => "Read views the page; Edit changes its content."
    };
}

public enum AccessLevel
{
    NoAccess = 0,
    ReadOnly = 1,
    Create = 2,
    Edit = 3,
    Delete = 4,
    FullAccess = 5
}

public sealed record AuthorizedUserRecord(
    Guid Id,
    string UserId,
    string? Gid,
    string DisplayName,
    string UserType,
    bool IsActive,
    bool IsRegistered,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SaveAuthorizedUserRequest(
    Guid? Id,
    string UserId,
    string? Gid,
    string DisplayName,
    string UserType,
    bool IsActive,
    string? TemporaryPassword);

public sealed record RegisterAuthorizedUserRequest(string Identifier, string Password);

public sealed record RegistrationResult(bool Success, string Message);

public static class AccountTokenPurposes
{
    public const string PrivilegedInvitation = "PrivilegedInvitation";
    public const string PasswordReset = "PasswordReset";
}

public sealed record AccountTokenIssueResult(
    string Token,
    string Recipient,
    string DisplayName,
    DateTimeOffset ExpiresAt);

public sealed record AccountTokenValidationResult(
    bool IsValid,
    string? DisplayName,
    string Message);

public sealed record ModulePermissionRecord(string Module, string UserType, AccessLevel AccessLevel);
public sealed record UserModulePermissionRecord(Guid AuthorizedUserId, string Module, AccessLevel? AccessLevel);

public interface IUserAccessService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task EnsureModulesAsync(IReadOnlyCollection<string> modules, CancellationToken cancellationToken = default);
    Task RemoveModulesAsync(IReadOnlyCollection<string> modules, CancellationToken cancellationToken = default);
    Task<AuthorizedUserRecord?> ValidateCredentialsAsync(string identifier, string password, CancellationToken cancellationToken = default);
    Task<AuthorizedUserRecord?> FindActiveUserAsync(string identifier, CancellationToken cancellationToken = default);
    Task<RegistrationResult> RegisterAsync(RegisterAuthorizedUserRequest request, CancellationToken cancellationToken = default);
    Task<AccountTokenIssueResult> CreatePrivilegedInvitationAsync(
        Guid userId,
        string? actorUserId,
        CancellationToken cancellationToken = default);
    Task<AccountTokenIssueResult?> CreatePasswordResetAsync(
        string identifier,
        CancellationToken cancellationToken = default);
    Task<AccountTokenValidationResult> ValidateAccountTokenAsync(
        string token,
        string purpose,
        CancellationToken cancellationToken = default);
    Task<RegistrationResult> SetPasswordWithTokenAsync(
        string token,
        string purpose,
        string password,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuthorizedUserRecord>> ListUsersAsync(CancellationToken cancellationToken = default);
    Task<AuthorizedUserRecord?> GetUserAsync(Guid id, CancellationToken cancellationToken = default);
    Task<AuthorizedUserRecord> SaveUserAsync(SaveAuthorizedUserRequest request, string? actorUserId, CancellationToken cancellationToken = default);
    Task SetUserActiveAsync(Guid id, bool isActive, string? actorUserId, CancellationToken cancellationToken = default);
    Task DeleteUserAsync(Guid id, string? actorUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModulePermissionRecord>> ListPermissionsAsync(CancellationToken cancellationToken = default);
    Task SetPermissionsAsync(IReadOnlyCollection<ModulePermissionRecord> permissions, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserModulePermissionRecord>> ListUserPermissionsAsync(CancellationToken cancellationToken = default);
    Task SetUserPermissionsAsync(IReadOnlyCollection<UserModulePermissionRecord> permissions, CancellationToken cancellationToken = default);
    Task<AccessLevel> GetAccessLevelAsync(string module, string userType, CancellationToken cancellationToken = default);
    Task<AccessLevel> GetAccessLevelAsync(string module, string userType, Guid? authorizedUserId, CancellationToken cancellationToken = default);
}

public static class AccessLevelExtensions
{
    public static bool Allows(this AccessLevel granted, AccessLevel required) =>
        granted == AccessLevel.FullAccess || granted >= required;

    public static string ToDisplayName(this AccessLevel level) => level switch
    {
        AccessLevel.NoAccess => "No Access",
        AccessLevel.ReadOnly => "Read Only",
        AccessLevel.FullAccess => "Full Access",
        _ => level.ToString()
    };
}
