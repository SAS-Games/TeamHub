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
    public const string FlowDesigner = "Flow Designer";
    public const string StudioConfiguration = "Studio Configuration";
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
        FlowDesigner,
        StudioConfiguration,
        Administration,
        UserManagement,
        AccessManagement
    ];
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
    string DisplayName,
    string UserType,
    bool IsActive,
    bool IsRegistered,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SaveAuthorizedUserRequest(
    Guid? Id,
    string UserId,
    string DisplayName,
    string UserType,
    bool IsActive,
    string? TemporaryPassword);

public sealed record RegisterAuthorizedUserRequest(string UserId, string Password);

public sealed record RegistrationResult(bool Success, string Message);

public sealed record ModulePermissionRecord(string Module, string UserType, AccessLevel AccessLevel);

public interface IUserAccessService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task EnsureModulesAsync(IReadOnlyCollection<string> modules, CancellationToken cancellationToken = default);
    Task<AuthorizedUserRecord?> ValidateCredentialsAsync(string userId, string password, CancellationToken cancellationToken = default);
    Task<AuthorizedUserRecord?> FindActiveUserAsync(string userId, CancellationToken cancellationToken = default);
    Task<RegistrationResult> RegisterAsync(RegisterAuthorizedUserRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuthorizedUserRecord>> ListUsersAsync(CancellationToken cancellationToken = default);
    Task<AuthorizedUserRecord?> GetUserAsync(Guid id, CancellationToken cancellationToken = default);
    Task<AuthorizedUserRecord> SaveUserAsync(SaveAuthorizedUserRequest request, string? actorUserId, CancellationToken cancellationToken = default);
    Task SetUserActiveAsync(Guid id, bool isActive, string? actorUserId, CancellationToken cancellationToken = default);
    Task DeleteUserAsync(Guid id, string? actorUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModulePermissionRecord>> ListPermissionsAsync(CancellationToken cancellationToken = default);
    Task SetPermissionsAsync(IReadOnlyCollection<ModulePermissionRecord> permissions, CancellationToken cancellationToken = default);
    Task<AccessLevel> GetAccessLevelAsync(string module, string userType, CancellationToken cancellationToken = default);
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
