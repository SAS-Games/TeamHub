using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TeamHub.Web.Home;

namespace TeamHub.Tests;

public sealed class HomeContentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"teamhub-home-{Guid.NewGuid():N}");

    public HomeContentTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadsConfiguredTextImageAndLinks_WithRelativeOrAbsolutePaths(bool absolute)
    {
        await WriteContentAsync("Configured project");
        var content = await CreateService(absolute).GetContentAsync();

        Assert.Equal("Configured project", content.ProjectName);
        Assert.Equal("Configured vision", content.Vision);
        Assert.Equal("/images/home/project-background.svg", content.BackgroundImage);
        Assert.Equal("Team Wiki", Assert.Single(content.UsefulLinks).Label);
    }

    [Fact]
    public async Task RefreshReadsUpdatedFiles_WithoutRestartingService()
    {
        await WriteContentAsync("Original title");
        var service = CreateService();
        Assert.Equal("Original title", (await service.GetContentAsync()).ProjectName);

        await WriteContentAsync("Updated title");
        Assert.Equal("Updated title", (await service.GetContentAsync()).ProjectName);
    }

    [Fact]
    public async Task MissingFileReportsPath_AndRecoversWhenRestored()
    {
        var service = CreateService(absolute: true);
        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => service.GetContentAsync());
        Assert.Equal(Path.Combine(_root, "project.json"), exception.FileName);

        await WriteContentAsync("Restored title");
        Assert.Equal("Restored title", (await service.GetContentAsync()).ProjectName);
    }

    [Fact]
    public async Task DefaultConfigurationLoadsBundledFiles_WhenContentRootHasNoConfig()
    {
        var service = new HomeContentService(Options.Create(new HomeConfigurationOptions()), CreateEnvironment());
        var content = await service.GetContentAsync();

        Assert.False(string.IsNullOrWhiteSpace(content.ProjectName));
        Assert.False(string.IsNullOrWhiteSpace(content.Vision));
        Assert.Equal("/images/home/project-background.svg", content.BackgroundImage);
        Assert.NotEmpty(content.UsefulLinks);
    }

    private HomeContentService CreateService(bool absolute = false) => new(
        Options.Create(new HomeConfigurationOptions
        {
            ProjectInfoPath = absolute ? Path.Combine(_root, "project.json") : "project.json",
            UsefulLinksPath = absolute ? Path.Combine(_root, "links.json") : "links.json"
        }), CreateEnvironment());

    private IHostEnvironment CreateEnvironment() => new TestEnvironment { ContentRootPath = _root };

    private async Task WriteContentAsync(string title)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "project.json"), $$"""
            { "projectName": "{{title}}", "vision": "Configured vision", "backgroundImage": "/images/home/project-background.svg" }
            """);
        await File.WriteAllTextAsync(Path.Combine(_root, "links.json"), """
            [{ "label": "Team Wiki", "url": "https://example.com/wiki" }]
            """);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "TeamHub.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
