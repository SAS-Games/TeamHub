using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TeamHub.Web.Home;

public interface IHomeContentService
{
    Task<HomeContent> GetContentAsync(CancellationToken cancellationToken = default);
}

/// Loads the Home module's content from config/Home (project-info.json + useful-links.json), no database dependency.
public sealed class HomeContentService(IOptions<HomeConfigurationOptions> options) : IHomeContentService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private HomeContent? _cached;

    public async Task<HomeContent> GetContentAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        var content = await LoadProjectInfoAsync(cancellationToken);
        content.UsefulLinks = await LoadUsefulLinksAsync(cancellationToken);
        _cached = content;
        return _cached;
    }

    private async Task<HomeContent> LoadProjectInfoAsync(CancellationToken cancellationToken)
    {
        var path = options.Value.ProjectInfoPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new HomeContent();
        }

        await using var stream = File.OpenRead(path);
        var content = await JsonSerializer.DeserializeAsync<HomeContent>(stream, SerializerOptions, cancellationToken);
        return content ?? new HomeContent();
    }

    private async Task<List<UsefulLink>> LoadUsefulLinksAsync(CancellationToken cancellationToken)
    {
        var path = options.Value.UsefulLinksPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        var links = await JsonSerializer.DeserializeAsync<List<UsefulLink>>(stream, SerializerOptions, cancellationToken);
        return links ?? [];
    }
}
