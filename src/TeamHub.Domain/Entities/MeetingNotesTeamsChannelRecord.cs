namespace TeamHub.Domain.Entities;

public sealed class MeetingNotesTeamsChannelRecord
{
    public string StudioId { get; set; } = string.Empty;
    public string ChannelName { get; set; } = string.Empty;
    public string WebhookUrlProtected { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}
