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

public sealed class TeamHubFlowPermissionService(IHttpContextAccessor httpContextAccessor) : IFlowPermissionService
{
    public bool CanView(string? ownerId) => HasFullAccess() || IsOwner(ownerId);

    public bool CanViewShared() => httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated == true;

    public bool CanEdit(string? ownerId) => CurrentAccessLevel().Allows(AccessLevel.Edit) && (HasFullAccess() || IsOwner(ownerId));

    public bool CanDelete(string? ownerId) => CurrentAccessLevel().Allows(AccessLevel.Delete) && (HasFullAccess() || IsOwner(ownerId));

    public bool CanCreate() => CurrentAccessLevel().Allows(AccessLevel.Create);

    public bool CanUseTemplate(FlowTemplate template) =>
        template is not FlowTemplate.Onboarding || HasFullAccess();

    public bool CanUseDiagramType(DiagramType diagramType) => diagramType != DiagramType.WorkCenterWorkflow || HasFullAccess();

    public bool CanManageTemplates() => HasFullAccess();

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
