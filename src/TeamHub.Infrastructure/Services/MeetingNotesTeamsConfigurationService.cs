using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TeamHub.AI.Contracts;
using TeamHub.Domain.Entities;
using TeamHub.Infrastructure.Persistence;

namespace TeamHub.Infrastructure.Services;

internal sealed class MeetingNotesTeamsConfigurationService(
    WorkflowDbContext dbContext,
    IDataProtectionProvider dataProtectionProvider) : IMeetingNotesTeamsConfigurationService
{
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector(
        "TeamHub.AI.MeetingNotes.TeamsWebhook.v1");

    public async Task<IReadOnlyList<MeetingNotesTeamsChannelStatus>> ListAsync(
        CancellationToken cancellationToken = default) =>
        await dbContext.MeetingNotesTeamsChannels.AsNoTracking()
            .OrderBy(item => item.StudioId)
            .Select(item => new MeetingNotesTeamsChannelStatus(
                item.StudioId,
                item.ChannelName,
                item.WebhookUrlProtected != string.Empty))
            .ToListAsync(cancellationToken);

    public async Task SaveAsync(
        IReadOnlyList<SaveMeetingNotesTeamsChannel> channels,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var actor = updatedBy?.Trim() ?? string.Empty;
        if (actor.Length == 0) throw new ArgumentException("The administrator identity is required.");
        if (channels.Count > 500) throw new ArgumentException("Too many Teams channel rows were submitted.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existing = await dbContext.MeetingNotesTeamsChannels.ToListAsync(cancellationToken);
        var byStudio = existing.ToDictionary(item => item.StudioId, StringComparer.OrdinalIgnoreCase);
        foreach (var change in channels)
        {
            var studioId = change.StudioId?.Trim() ?? string.Empty;
            var channelName = change.ChannelName?.Trim() ?? string.Empty;
            var webhookValue = change.WebhookUrl?.Trim() ?? string.Empty;
            if (studioId.Length is 0 or > 100 || !ids.Add(studioId))
                throw new ArgumentException("Every Teams channel row must have a unique valid studio.");

            byStudio.TryGetValue(studioId, out var record);
            if (change.Remove)
            {
                if (record is not null) dbContext.MeetingNotesTeamsChannels.Remove(record);
                continue;
            }

            if (channelName.Length is 0 or > 200)
            {
                if (record is not null || webhookValue.Length > 0)
                    throw new ArgumentException($"Enter a channel name for studio '{studioId}'.");
                continue;
            }

            if (record is null && webhookValue.Length == 0) continue;
            if (webhookValue.Length > 0)
            {
                var webhook = ParseMicrosoftWorkflowUrl(webhookValue);
                record ??= new MeetingNotesTeamsChannelRecord { StudioId = studioId };
                record.WebhookUrlProtected = protector.Protect(webhook.AbsoluteUri);
                if (record.StudioId == studioId && dbContext.Entry(record).State == EntityState.Detached)
                    dbContext.MeetingNotesTeamsChannels.Add(record);
            }

            if (record is null) continue;
            record.ChannelName = channelName;
            record.UpdatedAtUtc = DateTime.UtcNow;
            record.UpdatedBy = actor;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<Uri?> GetWebhookAsync(
        string studioId,
        CancellationToken cancellationToken = default)
    {
        var normalizedId = studioId?.Trim() ?? string.Empty;
        if (normalizedId.Length == 0) return null;
        var protectedValue = await dbContext.MeetingNotesTeamsChannels.AsNoTracking()
            .Where(item => item.StudioId == normalizedId)
            .Select(item => item.WebhookUrlProtected)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(protectedValue)) return null;
        try
        {
            return ParseMicrosoftWorkflowUrl(protector.Unprotect(protectedValue));
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException(
                "The Teams Workflow webhook cannot be decrypted. Ask an administrator to enter it again.",
                exception);
        }
    }

    private static Uri ParseMicrosoftWorkflowUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !IsMicrosoftWorkflowHost(uri.Host))
            throw new ArgumentException("Enter a valid Microsoft Teams Workflow webhook URL.");
        return uri;
    }

    private static bool IsMicrosoftWorkflowHost(string host)
    {
        var normalized = host.TrimEnd('.');
        return normalized.EndsWith(".logic.azure.com", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".powerautomate.com", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".powerplatform.com", StringComparison.OrdinalIgnoreCase);
    }
}
