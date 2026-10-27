using System.Net.Http.Json;
using System.Text;
using TeamHub.AI.Contracts;
using TeamHub.Studio;

namespace TeamHub.Web.AI;

public sealed record MeetingNotesTeamsDestination(string Id, string Studio);

public interface IMeetingNotesTeamsPublisher
{
    Task<IReadOnlyList<MeetingNotesTeamsDestination>> ListDestinationsAsync(
        CancellationToken cancellationToken = default);

    Task PublishAsync(
        string destinationId,
        string markdown,
        string publishedBy,
        CancellationToken cancellationToken = default);
}

public sealed class MeetingNotesTeamsPublisher(
    IAiConfigurationService configuration,
    IMeetingNotesTeamsConfigurationService teamsConfiguration,
    IStudioDirectoryService studios,
    HttpClient httpClient,
    ILogger<MeetingNotesTeamsPublisher> logger) : IMeetingNotesTeamsPublisher
{
    private const int MaximumMessageBytes = 28_000;

    public async Task<IReadOnlyList<MeetingNotesTeamsDestination>> ListDestinationsAsync(
        CancellationToken cancellationToken = default)
    {
        var activeStudios = (await studios.GetStudiosAsync(cancellationToken))
            .Where(studio => studio.IsActive)
            .ToDictionary(studio => studio.Id, StringComparer.OrdinalIgnoreCase);
        return (await teamsConfiguration.ListAsync(cancellationToken))
            .Where(destination => destination.IsConfigured && activeStudios.ContainsKey(destination.StudioId))
            .Select(destination => new MeetingNotesTeamsDestination(
                destination.StudioId,
                activeStudios[destination.StudioId].StudioName))
            .OrderBy(destination => destination.Studio, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task PublishAsync(
        string destinationId,
        string markdown,
        string publishedBy,
        CancellationToken cancellationToken = default)
    {
        var normalizedId = destinationId?.Trim() ?? string.Empty;
        var content = markdown?.Trim() ?? string.Empty;
        if (normalizedId.Length == 0) throw new ArgumentException("Select a studio destination.");
        if (content.Length == 0) throw new ArgumentException("Generate meeting notes before publishing.");
        if (Encoding.UTF8.GetByteCount(content) > MaximumMessageBytes)
            throw new ArgumentException("The meeting notes exceed the 28 KB Teams message limit. Download the Markdown file and attach it in Teams instead.");

        var options = (await configuration.GetAsync(cancellationToken)).Chat.MeetingNotes;
        if (!options.Enabled) throw new InvalidOperationException("Meeting Notes mode is disabled.");
        var studio = await studios.GetStudioAsync(normalizedId, cancellationToken);
        if (studio is null || !studio.IsActive)
            throw new ArgumentException("The selected studio is not active.");
        var destination = (await teamsConfiguration.ListAsync(cancellationToken)).SingleOrDefault(item =>
            item.IsConfigured && string.Equals(item.StudioId, normalizedId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("The selected studio does not have a Teams destination configured.");
        var webhook = await teamsConfiguration.GetWebhookAsync(destination.StudioId, cancellationToken);
        if (webhook is null)
            throw new InvalidOperationException("The selected Teams destination webhook is not available on this server.");

        using var response = await httpClient.PostAsJsonAsync(
            webhook,
            new { text = content },
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Publishing Meeting Notes to destination {DestinationId} by {PublishedBy} failed with status {StatusCode}.",
                destination.StudioId,
                publishedBy,
                (int)response.StatusCode);
            throw new InvalidOperationException(
                $"Teams rejected the meeting notes with HTTP {(int)response.StatusCode}.");
        }

        logger.LogInformation(
            "Meeting Notes were published to destination {DestinationId} by {PublishedBy}.",
            destination.StudioId,
            publishedBy);
    }
}
