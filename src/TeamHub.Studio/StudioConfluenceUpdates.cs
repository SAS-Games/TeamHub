using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
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

public sealed class ConsolidatedStudioConfluenceUpdateQuery
{
    public IReadOnlyList<string> StudioIds { get; set; } = [];
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
    public IReadOnlyList<string> UnconfiguredStudioIds { get; set; } = [];
}

public sealed class StudioConfluenceWeeklyUpdate
{
    public string StudioId { get; set; } = string.Empty;
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

public sealed class UpdateStudioConfluenceCommand
{
    public string StudioId { get; set; } = string.Empty;
    public string RequestingUserId { get; set; } = string.Empty;
    public string PageId { get; set; } = string.Empty;
    public DateOnly WeekStart { get; set; }
    public int ExpectedPageVersion { get; set; }
    public string StudioWork { get; set; } = string.Empty;
    public string HpgdsSupport { get; set; } = string.Empty;
    public string WmdSupport { get; set; } = string.Empty;
    public string ActionItems { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public sealed record UpdateStudioConfluenceResult(bool Succeeded, string Message, int? PageVersion = null);

public interface IStudioConfluenceUpdateService
{
    Task<StudioConfluenceUpdateResult> GetUpdatesAsync(StudioConfluenceUpdateQuery query, CancellationToken cancellationToken = default);
    Task<StudioConfluenceUpdateResult> GetConsolidatedUpdatesAsync(ConsolidatedStudioConfluenceUpdateQuery query, CancellationToken cancellationToken = default);
    Task<UpdateStudioConfluenceResult> UpdateAsync(UpdateStudioConfluenceCommand command, CancellationToken cancellationToken = default);
}

internal sealed class ConfluenceStudioUpdateService(
    HttpClient httpClient,
    IAtlassianConfigurationService configurationService,
    IAtlassianCredentialAccessor credentialAccessor) : IStudioConfluenceUpdateService
{
    private const int MaximumRangeDays = 370;
    private const int MaximumFieldLength = 20_000;

    public async Task<UpdateStudioConfluenceResult> UpdateAsync(
        UpdateStudioConfluenceCommand command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.RequestingUserId))
            return UpdateFailure("Sign in and connect your Confluence account before editing an update.");
        if (string.IsNullOrWhiteSpace(command.StudioId)
            || string.IsNullOrWhiteSpace(command.PageId)
            || command.ExpectedPageVersion < 1
            || command.WeekStart == default)
            return UpdateFailure("The Confluence update reference is invalid. Refresh the report and try again.");
        if (new[] { command.StudioWork, command.HpgdsSupport, command.WmdSupport, command.ActionItems, command.Notes }
            .Any(value => value?.Length > MaximumFieldLength))
            return UpdateFailure($"Each Confluence update field must be {MaximumFieldLength:N0} characters or fewer.");

        var settings = await configurationService.GetSettingsAsync(cancellationToken);
        if (!settings.ConfluenceEnabled || string.IsNullOrWhiteSpace(settings.ConfluenceBaseUrl))
            return UpdateFailure("Confluence integration is not configured.");

        var mapping = await configurationService.GetStudioMappingAsync(command.StudioId, cancellationToken);
        if (mapping is null || string.IsNullOrWhiteSpace(mapping.ConfluenceStudioIdentifier))
            return UpdateFailure("This studio does not have a Confluence table identifier.");

        AtlassianResolvedCredential? credential;
        try
        {
            // Writes must always use the person's own identity. Shared credentials are intentionally read only.
            credential = await credentialAccessor.ResolveConfluenceCredentialAsync(
                command.RequestingUserId,
                allowPrivilegedDefault: false,
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return UpdateFailure(exception.Message);
        }

        if (credential is null)
            return UpdateFailure("Connect your personal Confluence token from My Atlassian Connection before editing updates.");
        if (credential.IsReadOnly || credential.IsShared)
            return UpdateFailure("Shared Confluence credentials cannot edit weekly pages. Connect your personal token.");

        var contentUri = BuildContentItemUri(settings, command.PageId);
        using var getRequest = CreateAuthorizedRequest(HttpMethod.Get, $"{contentUri}?expand={Uri.EscapeDataString("body.storage,version,ancestors")}", credential.Token);
        using var getResponse = await httpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!getResponse.IsSuccessStatusCode)
            return UpdateFailure(BuildWriteHttpError(getResponse.StatusCode, getResponse.ReasonPhrase));

        ConfluencePage page;
        var isExpectedWeeklyPage = false;
        try
        {
            await using var stream = await getResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            page = new ConfluencePage(
                GetString(root, "id"),
                GetString(root, "title"),
                GetNestedString(root, "body", "storage", "value"),
                string.Empty,
                GetNestedInt(root, "version", "number"));
            var weekEnd = command.WeekStart.AddDays(4);
            var expectedTitle = StudioConfluencePageNaming.Format(settings.ConfluenceWeeklyTitlePattern, command.WeekStart, weekEnd);
            isExpectedWeeklyPage = string.Equals(page.Title, expectedTitle, StringComparison.OrdinalIgnoreCase)
                && new[] { command.WeekStart, weekEnd }.Any(anchor => MatchesAncestorPath(
                    root,
                    settings.ConfluenceParentPageId,
                    StudioConfluencePageNaming.Format(settings.ConfluenceYearTitlePattern, command.WeekStart, weekEnd, anchor),
                    StudioConfluencePageNaming.Format(settings.ConfluenceMonthTitlePattern, command.WeekStart, weekEnd, anchor)));
        }
        catch (JsonException)
        {
            return UpdateFailure("Confluence returned an unreadable page response. Refresh the report and try again.");
        }
        catch (FormatException)
        {
            return UpdateFailure("A configured Confluence page-title pattern contains an invalid date format.");
        }

        if (!string.Equals(page.Id, command.PageId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(page.Title) || page.Version < 1)
            return UpdateFailure("Confluence returned incomplete page data. Refresh the report and try again.");
        if (!isExpectedWeeklyPage)
            return UpdateFailure("The selected page is no longer in the configured Confluence weekly-report hierarchy. Refresh the report and try again.");
        if (page.Version != command.ExpectedPageVersion)
            return UpdateFailure("This Confluence page changed after the report was loaded. Refresh the report, review the latest content, and apply your edit again.");

        string updatedStorage;
        try
        {
            if (!StudioConfluenceTableParser.TryReplace(
                    page.StorageBody,
                    mapping.ConfluenceStudioIdentifier,
                    command.StudioWork,
                    command.HpgdsSupport,
                    command.WmdSupport,
                    command.ActionItems,
                    command.Notes,
                    out updatedStorage))
            {
                return UpdateFailure($"The Confluence row for '{mapping.ConfluenceStudioIdentifier}' could not be updated. Confirm that the weekly page still has Studio, Overview, and Notes columns.");
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return UpdateFailure("The Confluence table was too complex to update safely.");
        }

        var nextVersion = page.Version + 1;
        var payload = JsonSerializer.Serialize(new
        {
            id = page.Id,
            type = "page",
            title = page.Title,
            version = new { number = nextVersion, message = $"Updated {mapping.ConfluenceStudioIdentifier} from Team Hub" },
            body = new { storage = new { value = updatedStorage, representation = "storage" } }
        });
        using var putRequest = CreateAuthorizedRequest(HttpMethod.Put, contentUri, credential.Token);
        putRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var putResponse = await httpClient.SendAsync(putRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!putResponse.IsSuccessStatusCode)
        {
            var message = putResponse.StatusCode == HttpStatusCode.Conflict
                ? "Confluence rejected the save because the page changed. Refresh the report and apply your edit again."
                : BuildWriteHttpError(putResponse.StatusCode, putResponse.ReasonPhrase);
            return UpdateFailure(message);
        }

        return new UpdateStudioConfluenceResult(true, $"{mapping.ConfluenceStudioIdentifier} was updated in Confluence.", nextVersion);
    }

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
                : "Your Confluence token is not connected. Open My Connections and add your Bearer API token.");
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

    public async Task<StudioConfluenceUpdateResult> GetConsolidatedUpdatesAsync(
        ConsolidatedStudioConfluenceUpdateQuery query,
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
        if (string.IsNullOrWhiteSpace(query.RequestingUserId))
        {
            return NotConfigured("Sign in and connect your Confluence account to view weekly updates.");
        }

        var requestedStudioIds = query.StudioIds
            .Where(studioId => !string.IsNullOrWhiteSpace(studioId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (requestedStudioIds.Count == 0)
        {
            return new StudioConfluenceUpdateResult
            {
                IsConfigured = true,
                Message = "No studios are available for the consolidated report."
            };
        }

        var requestedIdSet = requestedStudioIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allMappings = await configurationService.ListStudioMappingsAsync(cancellationToken);
        var mappings = allMappings
            .Where(mapping => requestedIdSet.Contains(mapping.StudioId)
                && !string.IsNullOrWhiteSpace(mapping.ConfluenceStudioIdentifier))
            .ToList();
        var configuredIdSet = mappings.Select(mapping => mapping.StudioId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unconfiguredStudioIds = requestedStudioIds.Where(studioId => !configuredIdSet.Contains(studioId)).ToList();
        if (mappings.Count == 0)
        {
            return new StudioConfluenceUpdateResult
            {
                IsConfigured = true,
                Message = "None of the selected studios has a Confluence table identifier. Ask an administrator to configure the studio mappings.",
                UnconfiguredStudioIds = unconfiguredStudioIds
            };
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
                : "Your Confluence token is not connected. Open My Connections and add your Bearer API token.");
        }

        var updates = new List<StudioConfluenceWeeklyUpdate>();
        foreach (var weekStart in StudioConfluencePageNaming.EnumerateWeeks(startDate, endDate))
        {
            (IReadOnlyList<StudioConfluenceWeeklyUpdate> Updates, string? ErrorMessage) fetched;
            try
            {
                fetched = await FetchConsolidatedWeeklyUpdatesAsync(settings, mappings, credential, weekStart, cancellationToken);
            }
            catch (FormatException)
            {
                return ConsolidatedReadFailure("A configured Confluence page-title pattern contains an invalid date format.", credential, updates, unconfiguredStudioIds);
            }
            catch (RegexMatchTimeoutException)
            {
                return ConsolidatedReadFailure("The Confluence table was too complex to parse safely.", credential, updates, unconfiguredStudioIds);
            }

            if (fetched.ErrorMessage is not null)
            {
                return ConsolidatedReadFailure(fetched.ErrorMessage, credential, updates, unconfiguredStudioIds);
            }
            updates.AddRange(fetched.Updates);
        }

        return new StudioConfluenceUpdateResult
        {
            IsConfigured = true,
            IsReadOnly = credential.IsReadOnly,
            IsUsingSharedCredential = credential.IsShared,
            Updates = updates,
            UnconfiguredStudioIds = unconfiguredStudioIds
        };
    }

    private async Task<(IReadOnlyList<StudioConfluenceWeeklyUpdate> Updates, string? ErrorMessage)> FetchConsolidatedWeeklyUpdatesAsync(
        AtlassianIntegrationSettings settings,
        IReadOnlyList<StudioAtlassianMapping> mappings,
        AtlassianResolvedCredential credential,
        DateOnly weekStart,
        CancellationToken cancellationToken)
    {
        var weekEnd = weekStart.AddDays(4);
        var pageTitle = StudioConfluencePageNaming.Format(settings.ConfluenceWeeklyTitlePattern, weekStart, weekEnd);
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
                    : "Confluence rejected your personal token. Update it from My Connections and confirm that your account can access the configured space."
                : $"Confluence returned {(int)response.StatusCode} {response.ReasonPhrase}. Check the Confluence URL, API path, and space configuration.";
            return ([], message);
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var page = FindPage(document.RootElement, settings, weekStart, weekEnd);
            var updates = new List<StudioConfluenceWeeklyUpdate>(mappings.Count);
            foreach (var mapping in mappings)
            {
                var update = new StudioConfluenceWeeklyUpdate
                {
                    StudioId = mapping.StudioId,
                    StudioIdentifier = mapping.ConfluenceStudioIdentifier,
                    WeekStart = weekStart,
                    WeekEnd = weekEnd,
                    PageTitle = pageTitle
                };

                if (page is null)
                {
                    update.Message = $"Page not found below the configured Activities/year/month path: {pageTitle}.";
                    updates.Add(update);
                    continue;
                }

                update.PageFound = true;
                update.PageId = page.Id;
                update.PageVersion = page.Version;
                update.PageTitle = page.Title;
                update.PageUrl = BuildPageUrl(settings.ConfluenceBaseUrl, page.Id, page.WebUiPath);
                if (!StudioConfluenceTableParser.TryParse(page.StorageBody, mapping.ConfluenceStudioIdentifier, out var parsed, settings.ConfluenceBaseUrl))
                {
                    update.Message = $"No table row matched studio identifier '{mapping.ConfluenceStudioIdentifier}'.";
                    updates.Add(update);
                    continue;
                }

                update.StudioFound = true;
                update.Overview = parsed.Overview;
                update.StudioWork = parsed.StudioWork;
                update.HpgdsSupport = parsed.HpgdsSupport;
                update.WmdSupport = parsed.WmdSupport;
                update.ActionItems = parsed.ActionItems;
                update.Notes = parsed.Notes;
                updates.Add(update);
            }
            return (updates, null);
        }
        catch (JsonException)
        {
            return ([], "Confluence returned an unreadable response. Confirm that the configured API path returns JSON content data.");
        }
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
            StudioId = mapping.StudioId,
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
                    : "Confluence rejected your personal token. Update it from My Connections and confirm that your account can access the configured space."
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
            if (!StudioConfluenceTableParser.TryParse(page.StorageBody, mapping.ConfluenceStudioIdentifier, out var parsed, settings.ConfluenceBaseUrl))
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
        var hierarchyPaths = new[] { weekStart, weekEnd }
            .Select(anchor => (
                YearTitle: StudioConfluencePageNaming.Format(settings.ConfluenceYearTitlePattern, weekStart, weekEnd, anchor),
                MonthTitle: StudioConfluencePageNaming.Format(settings.ConfluenceMonthTitlePattern, weekStart, weekEnd, anchor)))
            .Distinct()
            .ToList();

        foreach (var item in results.EnumerateArray())
        {
            if (!hierarchyPaths.Any(path => MatchesAncestorPath(
                    item,
                    settings.ConfluenceParentPageId,
                    path.YearTitle,
                    path.MonthTitle))) continue;
            var body = GetNestedString(item, "body", "view", "value");
            if (string.IsNullOrWhiteSpace(body)) body = GetNestedString(item, "body", "storage", "value");
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
        var expand = Uri.EscapeDataString("body.storage,body.view,ancestors,version");
        return $"{settings.ConfluenceBaseUrl.TrimEnd('/')}{settings.ConfluenceContentApiPath}?spaceKey={Uri.EscapeDataString(settings.ConfluenceSpaceKey)}&title={Uri.EscapeDataString(pageTitle)}&expand={expand}&limit=50";
    }

    private static string BuildContentItemUri(AtlassianIntegrationSettings settings, string pageId) =>
        $"{settings.ConfluenceBaseUrl.TrimEnd('/')}/{settings.ConfluenceContentApiPath.Trim('/')}/{Uri.EscapeDataString(pageId)}";

    private static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Atlassian-Token", "no-check");
        return request;
    }

    private static string BuildWriteHttpError(HttpStatusCode statusCode, string? reasonPhrase) =>
        statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? "Confluence rejected the edit. Confirm that your personal token has permission to update the configured page."
            : statusCode == HttpStatusCode.NotFound
                ? "The Confluence page no longer exists or is no longer available to your account."
                : $"Confluence returned {(int)statusCode} {reasonPhrase} while saving the update.";

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

    private static UpdateStudioConfluenceResult UpdateFailure(string message) => new(false, message);

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

    private static StudioConfluenceUpdateResult ConsolidatedReadFailure(
        string message,
        AtlassianResolvedCredential credential,
        IReadOnlyList<StudioConfluenceWeeklyUpdate> updates,
        IReadOnlyList<string> unconfiguredStudioIds) =>
        new()
        {
            IsConfigured = true,
            IsReadOnly = credential.IsReadOnly,
            IsUsingSharedCredential = credential.IsShared,
            Message = message,
            Updates = updates,
            UnconfiguredStudioIds = unconfiguredStudioIds
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

    public static string Format(
        string? pattern,
        DateOnly weekStart,
        DateOnly weekEnd,
        DateOnly? hierarchyDate = null)
    {
        var source = string.IsNullOrWhiteSpace(pattern) ? "{WeekStart:dd/MM}-{WeekEnd:dd/MM}" : pattern;
        var hierarchyAnchor = hierarchyDate ?? weekStart;
        var formatted = DateTokenRegex().Replace(source, match =>
        {
            var date = match.Groups[1].Value == "WeekEnd" ? weekEnd : weekStart;
            var format = match.Groups[2].Success ? match.Groups[2].Value : "yyyy-MM-dd";
            return date.ToString(format, CultureInfo.InvariantCulture);
        });
        return formatted
            .Replace("{Year}", hierarchyAnchor.Year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{Month:00}", hierarchyAnchor.Month.ToString("00", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{Month}", hierarchyAnchor.Month.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
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
    public static bool TryParse(
        string storageBody,
        string studioIdentifier,
        out ParsedStudioConfluenceUpdate update,
        string? confluenceBaseUrl = null)
    {
        update = new ParsedStudioConfluenceUpdate(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(storageBody) || string.IsNullOrWhiteSpace(studioIdentifier)) return false;

        foreach (Match table in TableRegex().Matches(storageBody))
        {
            foreach (Match row in RowRegex().Matches(table.Groups[1].Value))
            {
                var cells = CellRegex().Matches(row.Groups[1].Value).Select(match => ToText(match.Groups[1].Value, confluenceBaseUrl)).ToList();
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

    public static bool TryReplace(
        string storageBody,
        string studioIdentifier,
        string? studioWork,
        string? hpgdsSupport,
        string? wmdSupport,
        string? actionItems,
        string? notes,
        out string updatedStorageBody)
    {
        updatedStorageBody = storageBody;
        if (string.IsNullOrWhiteSpace(storageBody) || string.IsNullOrWhiteSpace(studioIdentifier)) return false;

        foreach (Match table in TableRegex().Matches(storageBody))
        {
            foreach (Match row in RowRegex().Matches(table.Groups[1].Value))
            {
                var cells = CellRegex().Matches(row.Groups[1].Value).Cast<Match>().ToList();
                if (cells.Count < 3 || !string.Equals(ToText(cells[0].Groups[1].Value, null).Trim(), studioIdentifier.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;

                var overview = BuildOverviewStorage(studioWork, hpgdsSupport, wmdSupport, actionItems);
                var notesStorage = BuildTextStorage(notes);
                var rowContent = row.Groups[1].Value;
                rowContent = ReplaceGroupValue(rowContent, cells[2].Groups[1], notesStorage);
                rowContent = ReplaceGroupValue(rowContent, cells[1].Groups[1], overview);
                var absoluteRowContentStart = table.Groups[1].Index + row.Groups[1].Index;
                updatedStorageBody = string.Concat(
                    storageBody.AsSpan(0, absoluteRowContentStart),
                    rowContent,
                    storageBody.AsSpan(absoluteRowContentStart + row.Groups[1].Length));
                return true;
            }
        }
        return false;
    }

    private static string ReplaceGroupValue(string source, Group group, string replacement) =>
        string.Concat(source.AsSpan(0, group.Index), replacement, source.AsSpan(group.Index + group.Length));

    private static string BuildOverviewStorage(string? studioWork, string? hpgdsSupport, string? wmdSupport, string? actionItems) =>
        string.Concat(
            "<p><strong>Studio Work:</strong><br/>", BuildTextStorage(studioWork), "</p>",
            "<p><strong>HPGDS Support:</strong><br/>", BuildTextStorage(hpgdsSupport), "</p>",
            "<p><strong>WMD Support:</strong><br/>", BuildTextStorage(wmdSupport), "</p>",
            "<p><strong>Action Items:</strong><br/>", BuildTextStorage(actionItems), "</p>");

    private static string BuildTextStorage(string? value)
    {
        var normalized = (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return string.Join("<br/>", normalized.Split('\n').Select(EncodeLineWithLinks));
    }

    private static string EncodeLineWithLinks(string line)
    {
        var output = new StringBuilder(line.Length);
        var position = 0;
        foreach (Match match in MarkdownLinkRegex().Matches(line))
        {
            output.Append(HtmlEncoder.Default.Encode(line[position..match.Index]));
            var label = match.Groups[1].Value;
            var href = match.Groups[2].Value;
            if (Uri.TryCreate(href, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                output.Append("<a href=\"")
                    .Append(HtmlEncoder.Default.Encode(uri.AbsoluteUri))
                    .Append("\">")
                    .Append(HtmlEncoder.Default.Encode(label))
                    .Append("</a>");
            }
            else
            {
                output.Append(HtmlEncoder.Default.Encode(match.Value));
            }
            position = match.Index + match.Length;
        }
        output.Append(HtmlEncoder.Default.Encode(line[position..]));
        return output.ToString();
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

    private static string ToText(string html, string? confluenceBaseUrl)
    {
        var text = AnchorRegex().Replace(html, match => ToLinkText(match, confluenceBaseUrl));
        text = BreakRegex().Replace(text, "\n");
        text = BlockBoundaryRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace('\u00a0', ' ');
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line));
        return string.Join(Environment.NewLine, lines);
    }

    private static string ToLinkText(Match match, string? confluenceBaseUrl)
    {
        var label = TagRegex().Replace(match.Groups[3].Value, string.Empty);
        label = WebUtility.HtmlDecode(label).Trim();
        var href = WebUtility.HtmlDecode(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim();
        if (string.IsNullOrWhiteSpace(label) || !TryGetSafeLink(href, confluenceBaseUrl, out var link)) return label;
        return $"[{label}]({link})";
    }

    private static bool TryGetSafeLink(string href, string? confluenceBaseUrl, out string link)
    {
        link = string.Empty;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)
            && (!Uri.TryCreate(confluenceBaseUrl?.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
                || !Uri.TryCreate(baseUri, href, out uri)))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        link = uri.AbsoluteUri;
        return true;
    }

    [GeneratedRegex(@"<table\b[^>]*>(.*?)</table>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 2000)]
    private static partial Regex TableRegex();

    [GeneratedRegex(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 2000)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"<t[hd]\b[^>]*>(.*?)</t[hd]>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 2000)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"<br\b[^>]*?/?>", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex BreakRegex();

    [GeneratedRegex("<a\\b[^>]*\\bhref\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 2000)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"</?(?:p|div|li|h[1-6]|ul|ol|blockquote|pre|ac:task(?:-body|-list)?)(?:\s[^>]*)?/?>", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex BlockBoundaryRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline, 2000)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"(Studio Work|HPGDS Support|WMD Support|Action Items?)\s*:\s*", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex SectionLabelRegex();

    [GeneratedRegex(@"\[([^\]\r\n]+)\]\((https?://[^\s)]+)\)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex MarkdownLinkRegex();
}
