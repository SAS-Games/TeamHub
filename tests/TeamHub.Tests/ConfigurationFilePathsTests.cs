using Microsoft.Extensions.Configuration;
using TeamHub.Web.Options;

namespace TeamHub.Tests;

public sealed class ConfigurationFilePathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"teamhub-paths-{Guid.NewGuid():N}");

    public ConfigurationFilePathsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ExistingContentRootFileTakesPrecedence()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config", "Home"));
        var customFile = Path.Combine(_root, "config", "Home", "project-info.json");
        File.WriteAllText(customFile, "{}");

        Assert.Equal(customFile, ConfigurationFilePaths.Resolve("config/Home/project-info.json", _root));
    }

    [Fact]
    public void AbsoluteOverrideIsPreserved_EvenWhenFileIsMissing()
    {
        var customFile = Path.Combine(_root, "custom.xlsx");
        Assert.Equal(customFile, ConfigurationFilePaths.Resolve(customFile, _root));
    }

    [Fact]
    public void AppSettingsPathsResolveToRepositoryConfig_FromNestedWebContentRoot()
    {
        var contentRoot = Path.Combine(_root, "src", "TeamHub.Web");
        Directory.CreateDirectory(contentRoot);
        Directory.CreateDirectory(Path.Combine(_root, "config", "Home"));

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .Build();

        ConfigurationFilePaths.ResolveConfiguredPaths(configuration, contentRoot);

        var expectedPaths = new Dictionary<string, string>
        {
            ["WorkflowConfiguration:ExcelPath"] = Path.Combine(_root, "config", "Workflows.xlsx"),
            ["MilestoneConfiguration:ExcelPath"] = Path.Combine(_root, "config", "MilestoneTracker.xlsx"),
            ["HomeConfiguration:ProjectInfoPath"] = Path.Combine(_root, "config", "Home", "project-info.json"),
            ["HomeConfiguration:UsefulLinksPath"] = Path.Combine(_root, "config", "Home", "useful-links.json")
        };

        foreach (var (key, expectedPath) in expectedPaths)
        {
            Assert.Equal(expectedPath, configuration[key]);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
