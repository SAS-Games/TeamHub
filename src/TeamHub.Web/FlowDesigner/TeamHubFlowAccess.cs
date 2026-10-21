using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;
using TeamHub.Authentication;

namespace TeamHub.Web.FlowDesigner;

public sealed class TeamHubFlowCurrentUserProvider(IHttpContextAccessor httpContextAccessor) : ICurrentUserProvider
{
    public string? GetCurrentUserId()
    {
        var user = httpContextAccessor.HttpContext?.User;
        return user?.Identity?.IsAuthenticated == true ? user.Identity.Name : null;
    }
}

public sealed class TeamHubFlowReviewerDirectory(IUserAccessService users) : IFlowReviewerDirectory
{
    public async Task<IReadOnlyList<FlowReviewerOption>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<FlowReviewerOption>();
        foreach (var user in (await users.ListUsersAsync(cancellationToken))
                     .Where(user => user.IsActive && user.IsRegistered && user.UserType != TeamHubUserTypes.Admin))
        {
            var access = await users.GetAccessLevelAsync(
                TeamHubModules.FlowDesigner, user.UserType, user.Id, cancellationToken);
            if (access.Allows(AccessLevel.ReadOnly))
                result.Add(new(user.UserId.Trim().ToLowerInvariant(), user.DisplayName));
        }
        return result.OrderBy(user => user.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public sealed class TeamHubFlowPermissionService(IHttpContextAccessor httpContextAccessor) : IFlowPermissionService
{
    public bool CanView(string? ownerId) => HasFullAccess() || IsOwner(ownerId);

    public bool CanViewShared() => httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated == true;

    public bool CanEdit(string? ownerId) => CurrentAccessLevel().Allows(AccessLevel.Edit) && (HasFullAccess() || IsOwner(ownerId));

    public bool CanDelete(string? ownerId)
    {
        var accessLevel = CurrentAccessLevel();
        return IsOwner(ownerId)
            ? accessLevel.Allows(AccessLevel.Edit)
            : accessLevel == AccessLevel.FullAccess;
    }

    public bool CanCreate() => CurrentAccessLevel().Allows(AccessLevel.Create);

    public bool CanUseTemplate(FlowTemplate template) =>
        template is not FlowTemplate.Onboarding || HasFullAccess();

    public bool CanUseDiagramType(DiagramType diagramType) => diagramType != DiagramType.WorkCenterWorkflow || HasFullAccess();

    public bool CanManageTemplates() => HasFullAccess();

    public bool CanReviewPublications() =>
        httpContextAccessor.HttpContext?.User.IsInRole(TeamHubUserTypes.Admin) == true;

    private bool HasFullAccess() => CurrentAccessLevel() == AccessLevel.FullAccess;

    private AccessLevel CurrentAccessLevel() =>
        httpContextAccessor.HttpContext?.Items["TeamHub.AccessLevel"] is AccessLevel level
            ? level
            : AccessLevel.NoAccess;

    private bool IsOwner(string? ownerId)
    {
        var currentUser = httpContextAccessor.HttpContext?.User.Identity?.Name;
        return !string.IsNullOrWhiteSpace(ownerId)
            && !string.IsNullOrWhiteSpace(currentUser)
            && string.Equals(ownerId, currentUser, StringComparison.OrdinalIgnoreCase);
    }
}
