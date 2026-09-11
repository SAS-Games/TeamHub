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
            "HomeConfiguration:UsefulLinksPath"
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
        if (Path.IsPathRooted(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var contentRootPath = Path.GetFullPath(contentRoot);
        var isConfigurationPath = configuredPath.StartsWith($"config{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || configuredPath.StartsWith($"config{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

        for (var directory = new DirectoryInfo(contentRootPath); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.GetFullPath(configuredPath, directory.FullName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            // A repository/deployment config directory is authoritative even when an optional file is absent.
            if (isConfigurationPath && Directory.Exists(Path.Combine(directory.FullName, "config")))
            {
                return candidate;
            }
        }

        return Path.GetFullPath(configuredPath, contentRootPath);
    }
}
