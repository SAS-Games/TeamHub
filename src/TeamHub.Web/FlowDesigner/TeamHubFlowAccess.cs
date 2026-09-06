using TeamHub.FlowDesigner.Core.Contracts;

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
    public bool CanView(string? ownerId) => IsAdmin() || IsOwner(ownerId);

    public bool CanEdit(string? ownerId) => IsAdmin() || IsOwner(ownerId);

    public bool CanCreate() => httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated == true;

    private bool IsAdmin() => httpContextAccessor.HttpContext?.User.IsInRole("Admin") == true;

    private bool IsOwner(string? ownerId)
    {
        var currentUser = httpContextAccessor.HttpContext?.User.Identity?.Name;
        return !string.IsNullOrWhiteSpace(ownerId)
            && !string.IsNullOrWhiteSpace(currentUser)
            && string.Equals(ownerId, currentUser, StringComparison.OrdinalIgnoreCase);
    }
}
