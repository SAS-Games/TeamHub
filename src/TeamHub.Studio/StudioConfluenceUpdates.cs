using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TeamHub.Studio;

public sealed class StudioConfluenceUpdateQuery
{
    public string StudioId { get; set; } = string.Empty;
    public string RequestingUserId { get; set; } = string.Empty;
    public bool AllowPrivilegedDefaultCredential { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
}

public sealed class StudioConfluenceUpdateResult
{
    public bool IsConfigured { get; set; }
    public string? Message { get; set; }
    public bool IsReadOnly { get; set; } = true;
    public bool IsUsingSharedCredential { get; set; }
    public IReadOnlyList<StudioConfluenceWeeklyUpdate> Updates { get; set; } = [];
}

public sealed class StudioConfluenceWeeklyUpdate
{
    public DateOnly WeekStart { get; set; }
    public DateOnly WeekEnd { get; set; }
    public string PageId { get; set; } = string.Empty;
    public int PageVersion { get; set; }
    public string PageTitle { get; set; } = string.Empty;
    public string PageUrl { get; set; } = string.Empty;
    public bool PageFound { get; set; }
    public bool StudioFound { get; set; }
    public string StudioIdentifier { get; set; } = string.Empty;
    public string Overview { get; set; } = string.Empty;
    public string StudioWork { get; set; } = string.Empty;
    public string HpgdsSupport { get; set; } = string.Empty;
    public string WmdSupport { get; set; } = string.Empty;
    public string ActionItems { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string? Message { get; set; }
}

public interface IStudioConfluenceUpdateService
{
    Task<StudioConfluenceUpdateResult> GetUpdatesAsync(StudioConfluenceUpdateQuery query, CancellationToken cancellationToken = default);
}

internal sealed class ConfluenceStudioUpdateService(
    HttpClient httpClient,
    IAtlassianConfigurationService configurationService,
    IAtlassianCredentialAccessor credentialAccessor) : IStudioConfluenceUpdateService
{
    private const int MaximumRangeDays = 370;

    public async Task<StudioConfluenceUpdateResult> GetUpdatesAsync(
        StudioConfluenceUpdateQuery query,
        CancellationToken cancellationToken = default)
    {
        var settings = await configurationService.GetSettingsAsync(cancellationToken);
        if (!settings.ConfluenceEnabled)
        {
            return NotConfigured("Confluence integration is disabled. An administrator can enable it in Configuration > Jira & Confluence.");
        }
        if (string.IsNullOrWhiteSpace(settings.ConfluenceBaseUrl)) return NotConfigured("The Confluence base URL is not configured.");
        if (string.IsNullOrWhiteSpace(settings.ConfluenceSpaceKey)) return NotConfigured("The global Confluence space key is not configured.");
        if (string.IsNullOrWhiteSpace(settings.ConfluenceParentPageId)) return NotConfigured("The Activities root page ID is not configured.");

        var mapping = await configurationService.GetStudioMappingAsync(query.StudioId, cancellationToken);
        if (mapping is null || string.IsNullOrWhiteSpace(mapping.ConfluenceStudioIdentifier))
        {
            return NotConfigured("This studio does not have a Confluence table identifier. Ask an administrator to configure it.");
        }
        if (string.IsNullOrWhiteSpace(query.RequestingUserId))
        {
            return NotConfigured("Sign in and connect your Confluence account to view weekly updates.");
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var currentWeekStart = StudioConfluencePageNaming.StartOfWeek(today);
        var startDate = query.StartDate ?? currentWeekStart;
        var endDate = query.EndDate ?? currentWeekStart.AddDays(4);
        if (startDate > endDate) return InvalidRange("Start date must be on or before end date.");
        if (endDate.DayNumber - startDate.DayNumber > MaximumRangeDays)
        {
            return InvalidRange("Select a date range of one year or less.");
        }

        AtlassianResolvedCredential? credential;
        try
        {
            credential = await credentialAccessor.ResolveConfluenceCredentialAsync(
                query.RequestingUserId,
                query.AllowPrivilegedDefaultCredential,
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return NotConfigured(exception.Message);
        }

        if (credential is null)
        {
            return NotConfigured(query.AllowPrivilegedDefaultCredential
                ? "No Confluence credential is available. Ask an administrator to grant your privileged account read-only Confluence access, or add a personal token."
                : "Your Confluence token is not connected. Open My Atlassian Connection and add your Bearer API token.");
        }

        var updates = new List<StudioConfluenceWeeklyUpdate>();
        foreach (var weekStart in StudioConfluencePageNaming.EnumerateWeeks(startDate, endDate))
        {
            (StudioConfluenceWeeklyUpdate Update, string? ErrorMessage) fetched;
            try
            {
                fetched = await FetchWeeklyUpdateAsync(settings, mapping, credential, weekStart, cancellationToken);
            }
            catch (FormatException)
            {
                return ReadFailure("A configured Confluence page-title pattern contains an invalid date format.", credential, updates);
            }
            catch (RegexMatchTimeoutException)
            {
                return ReadFailure("The Confluence table was too complex to parse safely.", credential, updates);
            }
            if (fetched.ErrorMessage is not null)
            {
                return new StudioConfluenceUpdateResult
                {
                    IsConfigured = true,
                    IsReadOnly = credential.IsReadOnly,
                    IsUsingSharedCredential = credential.IsShared,
                    Message = fetched.ErrorMessage,
                    Updates = updates
                };
            }
            updates.Add(fetched.Update);
        }

        return new StudioConfluenceUpdateResult
        {
            IsConfigured = true,
            IsReadOnly = credential.IsReadOnly,
            IsUsingSharedCredential = credential.IsShared,
            Updates = updates
        };
    }

    private async Task<(StudioConfluenceWeeklyUpdate Update, string? ErrorMessage)> FetchWeeklyUpdateAsync(
        AtlassianIntegrationSettings settings,
        StudioAtlassianMapping mapping,
        AtlassianResolvedCredential credential,
        DateOnly weekStart,
        CancellationToken cancellationToken)
    {
        var weekEnd = weekStart.AddDays(4);
        var pageTitle = StudioConfluencePageNaming.Format(settings.ConfluenceWeeklyTitlePattern, weekStart, weekEnd);
        var update = new StudioConfluenceWeeklyUpdate
        {
            WeekStart = weekStart,
            WeekEnd = weekEnd,
            PageTitle = pageTitle,
            StudioIdentifier = mapping.ConfluenceStudioIdentifier
        };

        var requestUri = BuildContentSearchUri(settings, pageTitle);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Atlassian-Token", "no-check");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? credential.IsShared
                    ? "Confluence rejected the shared read-only token. Ask an administrator to update the default Confluence credential."
                    : "Confluence rejected your personal token. Update it from My Atlassian Connection and confirm that your account can access the configured space."
                : $"Confluence returned {(int)response.StatusCode} {response.ReasonPhrase}. Check the Confluence URL, API path, and space configuration.";
            return (update, message);
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var page = FindPage(document.RootElement, settings, weekStart, weekEnd);
            if (page is null)
            {
                update.Message = $"Page not found below the configured Activities/year/month path: {pageTitle}.";
                return (update, null);
            }

            update.PageFound = true;
            update.PageId = page.Id;
            update.PageVersion = page.Version;
            update.PageTitle = page.Title;
            update.PageUrl = BuildPageUrl(settings.ConfluenceBaseUrl, page.Id, page.WebUiPath);
            if (!StudioConfluenceTableParser.TryParse(page.StorageBody, mapping.ConfluenceStudioIdentifier, out var parsed))
            {
                update.Message = $"The page was found, but no table row matched studio identifier '{mapping.ConfluenceStudioIdentifier}'.";
                return (update, null);
            }

            update.StudioFound = true;
            update.Overview = parsed.Overview;
            update.StudioWork = parsed.StudioWork;
            update.HpgdsSupport = parsed.HpgdsSupport;
            update.WmdSupport = parsed.WmdSupport;
            update.ActionItems = parsed.ActionItems;
            update.Notes = parsed.Notes;
            return (update, null);
        }
        catch (JsonException)
        {
            return (update, "Confluence returned an unreadable response. Confirm that the configured API path returns JSON content data.");
        }
    }

    private static ConfluencePage? FindPage(
        JsonElement root,
        AtlassianIntegrationSettings settings,
        DateOnly weekStart,
        DateOnly weekEnd)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;
        var yearTitle = StudioConfluencePageNaming.Format(settings.ConfluenceYearTitlePattern, weekStart, weekEnd);
        var monthTitle = StudioConfluencePageNaming.Format(settings.ConfluenceMonthTitlePattern, weekStart, weekEnd);

        foreach (var item in results.EnumerateArray())
        {
            if (!MatchesAncestorPath(item, settings.ConfluenceParentPageId, yearTitle, monthTitle)) continue;
            var body = GetNestedString(item, "body", "storage", "value");
            var webUi = item.TryGetProperty("_links", out var links) ? GetString(links, "webui") : string.Empty;
            return new ConfluencePage(GetString(item, "id"), GetString(item, "title"), body, webUi, GetNestedInt(item, "version", "number"));
        }
        return null;
    }

    private static bool MatchesAncestorPath(JsonElement page, string rootPageId, string yearTitle, string monthTitle)
    {
        if (!page.TryGetProperty("ancestors", out var ancestors) || ancestors.ValueKind != JsonValueKind.Array) return false;
        var ancestorList = ancestors.EnumerateArray().ToList();
        var rootIndex = ancestorList.FindIndex(item => string.Equals(GetString(item, "id"), rootPageId, StringComparison.Ordinal));
        if (rootIndex < 0) return false;
        var yearIndex = ancestorList.FindIndex(rootIndex + 1, item => string.Equals(GetString(item, "title"), yearTitle, StringComparison.OrdinalIgnoreCase));
        if (yearIndex < 0) return false;
        return ancestorList.FindIndex(yearIndex + 1, item => string.Equals(GetString(item, "title"), monthTitle, StringComparison.OrdinalIgnoreCase)) >= 0;
    }

    private static string BuildContentSearchUri(AtlassianIntegrationSettings settings, string pageTitle)
    {
        var expand = Uri.EscapeDataString("body.storage,ancestors,version");
        return $"{settings.ConfluenceBaseUrl.TrimEnd('/')}{settings.ConfluenceContentApiPath}?spaceKey={Uri.EscapeDataString(settings.ConfluenceSpaceKey)}&title={Uri.EscapeDataString(pageTitle)}&expand={expand}&limit=50";
    }

    private static string BuildPageUrl(string baseUrl, string pageId, string webUiPath)
    {
        if (Uri.TryCreate(webUiPath, UriKind.Absolute, out var absolute)) return absolute.ToString();
        if (!string.IsNullOrWhiteSpace(webUiPath)) return $"{baseUrl.TrimEnd('/')}/{webUiPath.TrimStart('/')}";
        return $"{baseUrl.TrimEnd('/')}/pages/viewpage.action?pageId={Uri.EscapeDataString(pageId)}";
    }

    private static string GetNestedString(JsonElement element, params string[] path)
    {
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element)) return string.Empty;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : string.Empty;
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetNestedInt(JsonElement element, string parentProperty, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(parentProperty, out var parent)
        && parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(propertyName, out var value)
        && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static StudioConfluenceUpdateResult NotConfigured(string message) =>
        new() { IsConfigured = false, Message = message };

    private static StudioConfluenceUpdateResult InvalidRange(string message) =>
        new() { IsConfigured = true, Message = message };

    private static StudioConfluenceUpdateResult ReadFailure(
        string message,
        AtlassianResolvedCredential credential,
        IReadOnlyList<StudioConfluenceWeeklyUpdate> updates) =>
        new()
        {
            IsConfigured = true,
            IsReadOnly = credential.IsReadOnly,
            IsUsingSharedCredential = credential.IsShared,
            Message = message,
            Updates = updates
        };

    private sealed record ConfluencePage(string Id, string Title, string StorageBody, string WebUiPath, int Version);
}

internal static partial class StudioConfluencePageNaming
{
    public static DateOnly StartOfWeek(DateOnly date)
    {
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysSinceMonday);
    }

    public static IEnumerable<DateOnly> EnumerateWeeks(DateOnly startDate, DateOnly endDate)
    {
        for (var week = StartOfWeek(startDate); week <= endDate; week = week.AddDays(7)) yield return week;
    }

    public static string Format(string? pattern, DateOnly weekStart, DateOnly weekEnd)
    {
        var source = string.IsNullOrWhiteSpace(pattern) ? "{WeekStart:dd/MM}-{WeekEnd:dd/MM}" : pattern;
        var formatted = DateTokenRegex().Replace(source, match =>
        {
            var date = match.Groups[1].Value == "WeekEnd" ? weekEnd : weekStart;
            var format = match.Groups[2].Success ? match.Groups[2].Value : "yyyy-MM-dd";
            return date.ToString(format, CultureInfo.InvariantCulture);
        });
        return formatted
            .Replace("{Year}", weekStart.Year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{Month:00}", weekStart.Month.ToString("00", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{Month}", weekStart.Month.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\{(WeekStart|WeekEnd)(?::([^}]+))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex DateTokenRegex();
}

internal sealed record ParsedStudioConfluenceUpdate(
    string Overview,
    string StudioWork,
    string HpgdsSupport,
    string WmdSupport,
    string ActionItems,
    string Notes);

internal static partial class StudioConfluenceTableParser
{
    public static bool TryParse(string storageBody, string studioIdentifier, out ParsedStudioConfluenceUpdate update)
    {
        update = new ParsedStudioConfluenceUpdate(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(storageBody) || string.IsNullOrWhiteSpace(studioIdentifier)) return false;

        foreach (Match table in TableRegex().Matches(storageBody))
        {
            foreach (Match row in RowRegex().Matches(table.Groups[1].Value))
            {
                var cells = CellRegex().Matches(row.Groups[1].Value).Select(match => ToText(match.Groups[1].Value)).ToList();
                if (cells.Count < 2 || !string.Equals(cells[0].Trim(), studioIdentifier.Trim(), StringComparison.OrdinalIgnoreCase)) continue;

                var overview = cells[1];
                var sections = ParseOverviewSections(overview);
                update = new ParsedStudioConfluenceUpdate(
                    overview,
                    sections.GetValueOrDefault("Studio Work", string.Empty),
                    sections.GetValueOrDefault("HPGDS Support", string.Empty),
                    sections.GetValueOrDefault("WMD Support", string.Empty),
                    sections.GetValueOrDefault("Action Items", string.Empty),
                    cells.Count > 2 ? cells[2] : string.Empty);
                return true;
            }
        }
        return false;
    }

    private static Dictionary<string, string> ParseOverviewSections(string overview)
    {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = SectionLabelRegex().Matches(overview);
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            var valueStart = match.Index + match.Length;
            var valueEnd = index + 1 < matches.Count ? matches[index + 1].Index : overview.Length;
            var label = NormalizeLabel(match.Groups[1].Value);
            sections[label] = overview[valueStart..valueEnd].Trim();
        }
        return sections;
    }

    private static string NormalizeLabel(string value) => value.Trim().ToUpperInvariant() switch
    {
        "STUDIO WORK" => "Studio Work",
        "HPGDS SUPPORT" => "HPGDS Support",
        "WMD SUPPORT" => "WMD Support",
        _ => "Action Items"
    };

    private static string ToText(string html)
    {
        var text = BreakRegex().Replace(html, "\n");
        text = BlockEndRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace('\u00a0', ' ');
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line));
        return string.Join(Environment.NewLine, lines);
    }

    [GeneratedRegex(@"<table\b[^>]*>(.*?)</table>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 2000)]
    private static partial Regex TableRegex();

    [GeneratedRegex(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 2000)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"<t[hd]\b[^>]*>(.*?)</t[hd]>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 2000)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"</(?:p|div|li|h[1-6])\s*>", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex BlockEndRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline, 2000)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"(?:^|\n)\s*(Studio Work|HPGDS Support|WMD Support|Action Items?)\s*:\s*", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex SectionLabelRegex();
}
