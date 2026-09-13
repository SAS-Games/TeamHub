namespace TeamHub.Web.AccessControl;

public static class CustomTeamTabAccess
{
    public const string ModulePrefix = "Team Tab: ";

    public static string ModuleForSlug(string slug) => ModulePrefix + slug.Trim().ToLowerInvariant();

    public static string? ResolveModule(PathString path)
    {
        if (!path.StartsWithSegments("/Team/Custom", out var remaining)) return null;
        var slug = remaining.Value?.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(slug) ? null : ModuleForSlug(slug);
    }
}
