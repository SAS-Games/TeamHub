using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using TeamHub.Authentication;

namespace TeamHub.Web.AccessControl;

public sealed record CurrentAccessSnapshot(string UserType, AuthorizedUserRecord? User);

public interface ICurrentAccessService
{
    Task<CurrentAccessSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default);
    Task<AccessLevel> GetAccessLevelAsync(string module, CancellationToken cancellationToken = default);
    Task<bool> CanAsync(string module, AccessLevel required = AccessLevel.ReadOnly, CancellationToken cancellationToken = default);
}

public sealed class CurrentAccessService(IHttpContextAccessor httpContextAccessor, IUserAccessService users) : ICurrentAccessService
{
    private const string SnapshotKey = "TeamHub.CurrentAccess";

    public async Task<CurrentAccessSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var context = httpContextAccessor.HttpContext;
        if (context?.Items[SnapshotKey] is CurrentAccessSnapshot cached) return cached;

        CurrentAccessSnapshot snapshot;
        if (context?.User.Identity?.IsAuthenticated != true)
        {
            snapshot = new(TeamHubUserTypes.Guest, null);
        }
        else
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? context.User.Identity.Name
                ?? string.Empty;
            var user = await users.FindActiveUserAsync(userId, cancellationToken);
            snapshot = user is null
                ? new(TeamHubUserTypes.Guest, null)
                : new(user.UserType, user);
        }

        if (context is not null) context.Items[SnapshotKey] = snapshot;
        return snapshot;
    }

    public async Task<AccessLevel> GetAccessLevelAsync(string module, CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentAsync(cancellationToken);
        return await users.GetAccessLevelAsync(module, current.UserType, cancellationToken);
    }

    public async Task<bool> CanAsync(string module, AccessLevel required = AccessLevel.ReadOnly, CancellationToken cancellationToken = default) =>
        (await GetAccessLevelAsync(module, cancellationToken)).Allows(required);
}

public sealed class TeamHubAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        ICurrentAccessService currentAccess,
        ITeamHubModuleCatalog moduleCatalog)
    {
        if (IsUnrestrictedPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        var snapshot = await currentAccess.GetCurrentAsync(context.RequestAborted);
        if (context.User.Identity?.IsAuthenticated == true && snapshot.User is null)
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            await DenyAsync(context, needsLogin: true);
            return;
        }

        if (snapshot.User is not null)
        {
            context.User = CreatePrincipal(context.User.Identity?.AuthenticationType, snapshot.User);
        }

        var module = moduleCatalog.Resolve(context);
        if (module is null)
        {
            await next(context);
            return;
        }

        var required = TeamHubAccessRoutes.ResolveRequiredAccess(context.Request);
        var granted = await currentAccess.GetAccessLevelAsync(module, context.RequestAborted);
        if (!granted.Allows(required))
        {
            await DenyAsync(context, snapshot.User is null);
            return;
        }

        context.Items["TeamHub.Module"] = module;
        context.Items["TeamHub.RequiredAccess"] = required;
        context.Items["TeamHub.AccessLevel"] = granted;
        await next(context);
    }

    private static ClaimsPrincipal CreatePrincipal(string? authenticationType, AuthorizedUserRecord user)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.UserId),
            new Claim(ClaimTypes.Name, user.UserId),
            new Claim(ClaimTypes.GivenName, user.DisplayName),
            new Claim(ClaimTypes.Role, user.UserType)
        ], authenticationType ?? CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    private static bool IsUnrestrictedPath(PathString path) =>
        path.StartsWithSegments("/Login")
        || path.StartsWithSegments("/Logout")
        || path.StartsWithSegments("/Register")
        || path.StartsWithSegments("/AccessDenied")
        || path.StartsWithSegments("/Error")
        || path.StartsWithSegments("/css")
        || path.StartsWithSegments("/js")
        || path.StartsWithSegments("/lib")
        || path.StartsWithSegments("/_content")
        || path.StartsWithSegments("/favicon.ico");

    private static Task DenyAsync(HttpContext context, bool needsLogin)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = needsLogin ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        var destination = needsLogin
            ? "/Login" + QueryString.Create("returnUrl", context.Request.PathBase + context.Request.Path + context.Request.QueryString)
            : "/AccessDenied";
        context.Response.Redirect(destination);
        return Task.CompletedTask;
    }
}

public static class TeamHubAccessRoutes
{
    public static string? ResolveModule(PathString path)
    {
        if (path.StartsWithSegments("/Administration/Users")) return TeamHubModules.UserManagement;
        if (path.StartsWithSegments("/Administration/Access")) return TeamHubModules.AccessManagement;
        if (path.StartsWithSegments("/Configuration/Templates")) return TeamHubModules.FlowDesigner;
        if (path.StartsWithSegments("/Configuration")) return TeamHubModules.Administration;
        if (path.StartsWithSegments("/Profile/Atlassian")) return TeamHubModules.AtlassianConnection;
        if (path.StartsWithSegments("/WorkCenter") || path.StartsWithSegments("/Workflows")) return TeamHubModules.WorkCenter;
        if (path.StartsWithSegments("/Milestones")) return TeamHubModules.Milestones;
        if (path.StartsWithSegments("/Flows") || path.StartsWithSegments("/api/flows")) return TeamHubModules.FlowDesigner;
        if (path.StartsWithSegments("/Studio")) return TeamHubModules.StudioConfiguration;
        if (path.StartsWithSegments("/Team")) return TeamHubModules.Team;
        if (path == "/" || path.StartsWithSegments("/Index") || path.StartsWithSegments("/Privacy")) return TeamHubModules.Home;
        return null;
    }

    public static AccessLevel ResolveRequiredAccess(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        var handler = request.Query["handler"].ToString();
        if (HttpMethods.IsDelete(request.Method) || handler.Contains("Delete", StringComparison.OrdinalIgnoreCase)) return AccessLevel.Delete;
        if (path.Contains("/publish", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/templates", StringComparison.OrdinalIgnoreCase) && !HttpMethods.IsGet(request.Method)
            || handler.Contains("Publish", StringComparison.OrdinalIgnoreCase)) return AccessLevel.FullAccess;
        if (HttpMethods.IsPost(request.Method)
            && (handler.Contains("Create", StringComparison.OrdinalIgnoreCase) || handler.Contains("Duplicate", StringComparison.OrdinalIgnoreCase))) return AccessLevel.Create;
        if (HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method) || HttpMethods.IsPatch(request.Method)) return AccessLevel.Edit;
        if (HttpMethods.IsGet(request.Method)
            && (path.EndsWith("/New", StringComparison.OrdinalIgnoreCase) || request.Query.ContainsKey("create"))) return AccessLevel.Create;
        return AccessLevel.ReadOnly;
    }

    public static string? ResolveNavigationModule(string page) => ResolveModule(new PathString(page));
}

public static class TeamHubAccessApplicationExtensions
{
    public static IApplicationBuilder UseTeamHubAccessControl(this IApplicationBuilder app) =>
        app.UseMiddleware<TeamHubAccessMiddleware>();
}
