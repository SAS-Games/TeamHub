using Microsoft.Extensions.Configuration;
using TeamHub.Web.Options;

namespace TeamHub.Tests;

public sealed class ConfigurationFilePathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"teamhub-paths-{Guid.NewGuid():N}");

    public ConfigurationFilePathsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ExistingContentRootFileTakesPrecedenceOverBundledCopy()
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
    public void AppSettingsPathsResolveToBundledFiles_FromAnUnrelatedContentRoot()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .Build();

        ConfigurationFilePaths.ResolveConfiguredPaths(configuration, _root);

        foreach (var key in new[]
        {
            "MilestoneConfiguration:ExcelPath",
            "HomeConfiguration:ProjectInfoPath",
            "HomeConfiguration:UsefulLinksPath",
            "StudioJiraConfiguration:ConfigPath"
        })
        {
            var path = configuration[key]!;
            Assert.StartsWith(AppContext.BaseDirectory, path);
            Assert.True(File.Exists(path), $"Missing bundled configuration for {key}: {path}");
        }

        // The optional workflow import workbook is not shipped in this repository.
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "config", "Workflows.xlsx"),
            configuration["WorkflowConfiguration:ExcelPath"]);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
