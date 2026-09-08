namespace TeamHub.Web.Options;

public static class ConfigurationFilePaths
{
    public static void ResolveConfiguredPaths(IConfiguration configuration, string contentRoot)
    {
        string[] keys =
        [
            "WorkflowConfiguration:ExcelPath",
            "MilestoneConfiguration:ExcelPath",
            "HomeConfiguration:ProjectInfoPath",
            "HomeConfiguration:UsefulLinksPath",
            "StudioJiraConfiguration:ConfigPath"
        ];

        foreach (var key in keys)
        {
            var path = configuration[key];
            if (!string.IsNullOrWhiteSpace(path))
            {
                configuration[key] = Resolve(path, contentRoot);
            }
        }
    }

    public static string Resolve(string configuredPath, string contentRoot)
    {
        var path = Path.GetFullPath(configuredPath, contentRoot);
        if (Path.IsPathRooted(configuredPath) || File.Exists(path))
        {
            return path;
        }

        // Build/publish copies the repository configuration beside the application.
        return Path.GetFullPath(configuredPath, AppContext.BaseDirectory);
    }
}
