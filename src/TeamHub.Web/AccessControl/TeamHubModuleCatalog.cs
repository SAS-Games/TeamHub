using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using TeamHub.Authentication;

namespace TeamHub.Web.AccessControl;

public interface ITeamHubModuleCatalog
{
    IReadOnlyList<string> Modules { get; }
    void Discover(IEnumerable<EndpointDataSource> dataSources);
    string? Resolve(HttpContext context);
}

public sealed partial class TeamHubModuleCatalog : ITeamHubModuleCatalog
{
    private static readonly HashSet<string> UnrestrictedPages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Login", "Logout", "Register", "AcceptInvitation", "ForgotPassword", "ResetPassword", "AccessDenied", "Error"
    };

    private readonly HashSet<string> modules = new(TeamHubModules.All, StringComparer.Ordinal);

    public IReadOnlyList<string> Modules
    {
        get
        {
            lock (modules)
            {
                return TeamHubModules.All
                    .Concat(modules.Except(TeamHubModules.All).OrderBy(item => item))
                    .ToList();
            }
        }
    }

    public void Discover(IEnumerable<EndpointDataSource> dataSources)
    {
        var discovered = dataSources
            .SelectMany(source => source.Endpoints)
            .Select(endpoint => endpoint.Metadata.GetMetadata<PageActionDescriptor>())
            .Where(descriptor => descriptor is not null)
            .Select(descriptor => ResolvePage(descriptor!.ViewEnginePath))
            .Where(module => module is not null)
            .Cast<string>();

        lock (modules)
        {
            modules.UnionWith(discovered);
        }
    }

    public string? Resolve(HttpContext context)
    {
        var configured = TeamHubAccessRoutes.ResolveModule(context.Request.Path);
        if (configured is not null) return configured;

        var page = context.GetEndpoint()?.Metadata.GetMetadata<PageActionDescriptor>();
        return page is null ? null : ResolvePage(page.ViewEnginePath);
    }

    public static string? ResolvePage(string? pagePath)
    {
        if (string.IsNullOrWhiteSpace(pagePath)) return null;

        var configured = TeamHubAccessRoutes.ResolveModule(new PathString(pagePath));
        if (configured is not null) return configured;

        var segment = pagePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(segment) || UnrestrictedPages.Contains(segment)) return null;
        if (string.Equals(segment, "Index", StringComparison.OrdinalIgnoreCase)) return TeamHubModules.Home;

        var words = WordBoundary().Replace(segment.Replace('-', ' ').Replace('_', ' '), "$1 $2");
        return char.ToUpperInvariant(words[0]) + words[1..];
    }

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex WordBoundary();
}
