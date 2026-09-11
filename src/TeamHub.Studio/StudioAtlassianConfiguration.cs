using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace TeamHub.Studio;

public sealed class AtlassianIntegrationSettings
{
    public bool JiraEnabled { get; set; }
    public string JiraBaseUrl { get; set; } = string.Empty;
    public string JiraSearchApiPath { get; set; } = "/rest/api/2/search";
    public int JiraMaxResults { get; set; } = 100;
    public string JiraDefaultSupportComponent { get; set; } = "studio_Support";
    public bool ConfluenceEnabled { get; set; }
    public string ConfluenceBaseUrl { get; set; } = string.Empty;
    public string ConfluenceContentApiPath { get; set; } = "/rest/api/content";
}

public sealed class StudioAtlassianMapping
{
    public string StudioId { get; set; } = string.Empty;
    public IReadOnlyList<string> JiraProjectKeys { get; set; } = [];
    public string JiraStudioComponent { get; set; } = string.Empty;
    public string JiraSupportComponent { get; set; } = string.Empty;
    public string ConfluenceSpaceKey { get; set; } = string.Empty;
    public string ConfluenceParentPageId { get; set; } = string.Empty;
    public string ConfluenceWeeklyTitlePattern { get; set; } = "{StudioName} Weekly Update - {WeekStart:yyyy-MM-dd}";
}

public sealed record AtlassianConnectionStatus(
    bool HasJiraToken,
    DateTime? JiraConnectedAtUtc,
    bool HasConfluenceToken,
    DateTime? ConfluenceConnectedAtUtc);

public interface IAtlassianConfigurationService
{
    Task<AtlassianIntegrationSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AtlassianIntegrationSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StudioAtlassianMapping>> ListStudioMappingsAsync(CancellationToken cancellationToken = default);
    Task<StudioAtlassianMapping?> GetStudioMappingAsync(string studioId, CancellationToken cancellationToken = default);
    Task SaveStudioMappingsAsync(IReadOnlyCollection<StudioAtlassianMapping> mappings, CancellationToken cancellationToken = default);
    Task<AtlassianConnectionStatus> GetConnectionStatusAsync(string userId, CancellationToken cancellationToken = default);
    Task SaveUserTokensAsync(
        string userId,
        string? jiraToken,
        string? confluenceToken,
        bool removeJiraToken = false,
        bool removeConfluenceToken = false,
        CancellationToken cancellationToken = default);
    Task MoveUserTokensAsync(string currentUserId, string newUserId, CancellationToken cancellationToken = default);
    Task DeleteUserTokensAsync(string userId, CancellationToken cancellationToken = default);
}

internal interface IAtlassianCredentialAccessor
{
    Task<string?> GetJiraTokenAsync(string userId, CancellationToken cancellationToken = default);
    Task<string?> GetConfluenceTokenAsync(string userId, CancellationToken cancellationToken = default);
}

internal sealed class SqliteAtlassianConfigurationService : IAtlassianConfigurationService, IAtlassianCredentialAccessor
{
    private const string TokenProtectorPurpose = "TeamHub.Atlassian.UserTokens.v1";
    private readonly StudioDbContext dbContext;
    private readonly IDataProtector tokenProtector;

    public SqliteAtlassianConfigurationService(StudioDbContext dbContext, IDataProtectionProvider dataProtectionProvider)
    {
        this.dbContext = dbContext;
        tokenProtector = dataProtectionProvider.CreateProtector(TokenProtectorPurpose);
    }

    public async Task<AtlassianIntegrationSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var record = await dbContext.AtlassianIntegrationSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        return record is null ? new AtlassianIntegrationSettings() : ToModel(record);
    }

    public async Task SaveSettingsAsync(AtlassianIntegrationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var jiraBaseUrl = NormalizeBaseUrl(settings.JiraBaseUrl, settings.JiraEnabled, "Jira");
        var confluenceBaseUrl = NormalizeBaseUrl(settings.ConfluenceBaseUrl, settings.ConfluenceEnabled, "Confluence");

        var record = await dbContext.AtlassianIntegrationSettings.SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        if (record is null)
        {
            record = new AtlassianIntegrationSettingsRecord { Id = 1 };
            dbContext.AtlassianIntegrationSettings.Add(record);
        }

        record.JiraEnabled = settings.JiraEnabled;
        record.JiraBaseUrl = jiraBaseUrl;
        record.JiraSearchApiPath = NormalizeApiPath(settings.JiraSearchApiPath, "/rest/api/2/search");
        record.JiraMaxResults = Math.Clamp(settings.JiraMaxResults, 1, 1000);
        record.JiraDefaultSupportComponent = settings.JiraDefaultSupportComponent?.Trim() ?? string.Empty;
        record.ConfluenceEnabled = settings.ConfluenceEnabled;
        record.ConfluenceBaseUrl = confluenceBaseUrl;
        record.ConfluenceContentApiPath = NormalizeApiPath(settings.ConfluenceContentApiPath, "/rest/api/content");
        record.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StudioAtlassianMapping>> ListStudioMappingsAsync(CancellationToken cancellationToken = default)
    {
        var records = await dbContext.StudioAtlassianMappings.AsNoTracking()
            .OrderBy(item => item.Studio.StudioName)
            .ToListAsync(cancellationToken);
        return records.Select(ToModel).ToList();
    }

    public async Task<StudioAtlassianMapping?> GetStudioMappingAsync(string studioId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(studioId, out var parsedId))
        {
            return null;
        }

        var record = await dbContext.StudioAtlassianMappings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.StudioRecordId == parsedId, cancellationToken);
        return record is null ? null : ToModel(record);
    }

    public async Task SaveStudioMappingsAsync(IReadOnlyCollection<StudioAtlassianMapping> mappings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var studioIds = await dbContext.Studios.AsNoTracking().Select(item => item.Id).ToHashSetAsync(cancellationToken);
        foreach (var mapping in mappings)
        {
            if (!Guid.TryParse(mapping.StudioId, out var studioId) || !studioIds.Contains(studioId))
            {
                continue;
            }

            var record = await dbContext.StudioAtlassianMappings
                .SingleOrDefaultAsync(item => item.StudioRecordId == studioId, cancellationToken);
            if (record is null)
            {
                record = new StudioAtlassianMappingRecord { StudioRecordId = studioId };
                dbContext.StudioAtlassianMappings.Add(record);
            }

            record.JiraProjectKeys = SerializeProjectKeys(mapping.JiraProjectKeys);
            record.JiraStudioComponent = mapping.JiraStudioComponent?.Trim() ?? string.Empty;
            record.JiraSupportComponent = mapping.JiraSupportComponent?.Trim() ?? string.Empty;
            record.ConfluenceSpaceKey = mapping.ConfluenceSpaceKey?.Trim() ?? string.Empty;
            record.ConfluenceParentPageId = mapping.ConfluenceParentPageId?.Trim() ?? string.Empty;
            record.ConfluenceWeeklyTitlePattern = string.IsNullOrWhiteSpace(mapping.ConfluenceWeeklyTitlePattern)
                ? "{StudioName} Weekly Update - {WeekStart:yyyy-MM-dd}"
                : mapping.ConfluenceWeeklyTitlePattern.Trim();
            record.UpdatedAtUtc = DateTime.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<AtlassianConnectionStatus> GetConnectionStatusAsync(string userId, CancellationToken cancellationToken = default)
    {
        var normalizedUserId = NormalizeUserId(userId);
        var record = await dbContext.AtlassianUserCredentials.AsNoTracking()
            .SingleOrDefaultAsync(item => item.NormalizedUserId == normalizedUserId, cancellationToken);
        return record is null
            ? new(false, null, false, null)
            : new(
                !string.IsNullOrWhiteSpace(record.JiraTokenProtected),
                record.JiraConnectedAtUtc,
                !string.IsNullOrWhiteSpace(record.ConfluenceTokenProtected),
                record.ConfluenceConnectedAtUtc);
    }

    public async Task SaveUserTokensAsync(
        string userId,
        string? jiraToken,
        string? confluenceToken,
        bool removeJiraToken = false,
        bool removeConfluenceToken = false,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = NormalizeUserId(userId);
        var record = await dbContext.AtlassianUserCredentials
            .SingleOrDefaultAsync(item => item.NormalizedUserId == normalizedUserId, cancellationToken);
        if (record is null)
        {
            record = new AtlassianUserCredentialRecord
            {
                UserId = userId.Trim(),
                NormalizedUserId = normalizedUserId
            };
            dbContext.AtlassianUserCredentials.Add(record);
        }

        if (removeJiraToken)
        {
            record.JiraTokenProtected = null;
            record.JiraConnectedAtUtc = null;
        }
        else if (!string.IsNullOrWhiteSpace(jiraToken))
        {
            record.JiraTokenProtected = tokenProtector.Protect(jiraToken.Trim());
            record.JiraConnectedAtUtc = DateTime.UtcNow;
        }

        if (removeConfluenceToken)
        {
            record.ConfluenceTokenProtected = null;
            record.ConfluenceConnectedAtUtc = null;
        }
        else if (!string.IsNullOrWhiteSpace(confluenceToken))
        {
            record.ConfluenceTokenProtected = tokenProtector.Protect(confluenceToken.Trim());
            record.ConfluenceConnectedAtUtc = DateTime.UtcNow;
        }

        record.UserId = userId.Trim();
        record.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MoveUserTokensAsync(string currentUserId, string newUserId, CancellationToken cancellationToken = default)
    {
        var currentNormalized = NormalizeUserId(currentUserId);
        var newNormalized = NormalizeUserId(newUserId);
        var record = await dbContext.AtlassianUserCredentials
            .SingleOrDefaultAsync(item => item.NormalizedUserId == currentNormalized, cancellationToken);
        if (record is null)
        {
            return;
        }

        record.UserId = newUserId.Trim();
        record.NormalizedUserId = newNormalized;
        record.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteUserTokensAsync(string userId, CancellationToken cancellationToken = default)
    {
        var normalizedUserId = NormalizeUserId(userId);
        var record = await dbContext.AtlassianUserCredentials
            .SingleOrDefaultAsync(item => item.NormalizedUserId == normalizedUserId, cancellationToken);
        if (record is null)
        {
            return;
        }

        dbContext.AtlassianUserCredentials.Remove(record);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<string?> GetJiraTokenAsync(string userId, CancellationToken cancellationToken = default) =>
        GetTokenAsync(userId, record => record.JiraTokenProtected, cancellationToken);

    public Task<string?> GetConfluenceTokenAsync(string userId, CancellationToken cancellationToken = default) =>
        GetTokenAsync(userId, record => record.ConfluenceTokenProtected, cancellationToken);

    private async Task<string?> GetTokenAsync(
        string userId,
        Func<AtlassianUserCredentialRecord, string?> selector,
        CancellationToken cancellationToken)
    {
        var normalizedUserId = NormalizeUserId(userId);
        var record = await dbContext.AtlassianUserCredentials.AsNoTracking()
            .SingleOrDefaultAsync(item => item.NormalizedUserId == normalizedUserId, cancellationToken);
        var protectedToken = record is null ? null : selector(record);
        if (string.IsNullOrWhiteSpace(protectedToken))
        {
            return null;
        }

        try
        {
            return tokenProtector.Unprotect(protectedToken);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("The stored Atlassian credential cannot be decrypted. Re-enter the token on your Atlassian connection page.");
        }
    }

    private static AtlassianIntegrationSettings ToModel(AtlassianIntegrationSettingsRecord record) => new()
    {
        JiraEnabled = record.JiraEnabled,
        JiraBaseUrl = record.JiraBaseUrl,
        JiraSearchApiPath = record.JiraSearchApiPath,
        JiraMaxResults = record.JiraMaxResults,
        JiraDefaultSupportComponent = record.JiraDefaultSupportComponent,
        ConfluenceEnabled = record.ConfluenceEnabled,
        ConfluenceBaseUrl = record.ConfluenceBaseUrl,
        ConfluenceContentApiPath = record.ConfluenceContentApiPath
    };

    private static StudioAtlassianMapping ToModel(StudioAtlassianMappingRecord record) => new()
    {
        StudioId = record.StudioRecordId.ToString(),
        JiraProjectKeys = ParseProjectKeys(record.JiraProjectKeys),
        JiraStudioComponent = record.JiraStudioComponent,
        JiraSupportComponent = record.JiraSupportComponent,
        ConfluenceSpaceKey = record.ConfluenceSpaceKey,
        ConfluenceParentPageId = record.ConfluenceParentPageId,
        ConfluenceWeeklyTitlePattern = record.ConfluenceWeeklyTitlePattern
    };

    private static string NormalizeBaseUrl(string? value, bool required, string systemName)
    {
        var trimmed = value?.Trim().TrimEnd('/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            if (required)
            {
                throw new ArgumentException($"{systemName} base URL is required when the integration is enabled.");
            }
            return string.Empty;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException($"Enter a valid HTTP or HTTPS {systemName} base URL.");
        }
        return trimmed;
    }

    private static string NormalizeApiPath(string? value, string fallback)
    {
        var path = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return path.StartsWith('/') ? path : $"/{path}";
    }

    private static string NormalizeUserId(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("A signed-in user is required.", nameof(userId));
        }
        return userId.Trim().ToUpperInvariant();
    }

    private static string SerializeProjectKeys(IEnumerable<string> values) =>
        string.Join(",", values
            .SelectMany(value => value.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyList<string> ParseProjectKeys(string value) =>
        value.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
