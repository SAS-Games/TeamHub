using System.Text.Json;
using Microsoft.Extensions.Options;
using TeamHub.Web.Options;

namespace TeamHub.Web.Home;

public interface IHomeContentService
{
    Task<HomeContent> GetContentAsync(CancellationToken cancellationToken = default);
}

/// Loads the Home module's content from config/Home (project-info.json + useful-links.json), no database dependency.
public sealed class HomeContentService(
    IOptions<HomeConfigurationOptions> options,
    IHostEnvironment environment) : IHomeContentService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<HomeContent> GetContentAsync(CancellationToken cancellationToken = default)
    {
        var content = await LoadProjectInfoAsync(cancellationToken);
        content.UsefulLinks = await LoadUsefulLinksAsync(cancellationToken);
        return content;
    }

    private async Task<HomeContent> LoadProjectInfoAsync(CancellationToken cancellationToken)
    {
        var path = ResolvePath(options.Value.ProjectInfoPath);
        await using var stream = File.OpenRead(path);
        var content = await JsonSerializer.DeserializeAsync<HomeContent>(stream, SerializerOptions, cancellationToken);
        return content ?? throw new InvalidDataException($"Home configuration '{path}' must contain a JSON object.");
    }

    private async Task<List<UsefulLink>> LoadUsefulLinksAsync(CancellationToken cancellationToken)
    {
        var path = ResolvePath(options.Value.UsefulLinksPath);
        await using var stream = File.OpenRead(path);
        var links = await JsonSerializer.DeserializeAsync<List<UsefulLink>>(stream, SerializerOptions, cancellationToken);
        return links ?? throw new InvalidDataException($"Home configuration '{path}' must contain a JSON array.");
    }

    private string ResolvePath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException("Home configuration file paths must not be empty.");
        }

        var path = ConfigurationFilePaths.Resolve(configuredPath, environment.ContentRootPath);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Home configuration file was not found: '{path}'. Check HomeConfiguration paths.", path);
        }

        return path;
    }
}
