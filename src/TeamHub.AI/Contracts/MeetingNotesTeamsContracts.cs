namespace TeamHub.AI.Contracts;

public sealed record MeetingNotesTeamsChannelStatus(
    string StudioId,
    bool IsConfigured);

public sealed record SaveMeetingNotesTeamsChannel(
    string StudioId,
    string? WebhookUrl,
    bool Remove);

public interface IMeetingNotesTeamsConfigurationService
{
    Task<IReadOnlyList<MeetingNotesTeamsChannelStatus>> ListAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        IReadOnlyList<SaveMeetingNotesTeamsChannel> channels,
        string updatedBy,
        CancellationToken cancellationToken = default);

    Task<Uri?> GetWebhookAsync(
        string studioId,
        CancellationToken cancellationToken = default);
}
