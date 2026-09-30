using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace TeamHub.Studio;

public sealed class StudioJiraTicketQuery
{
    public string StudioId { get; set; } = string.Empty;
    public string RequestingUserId { get; set; } = string.Empty;
    public bool AllowPrivilegedDefaultCredential { get; set; }
    public bool ActiveSprintOnly { get; set; } = true;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public bool UseSprintDateRange { get; set; }
}

public sealed class StudioJiraTicketBatchQuery
{
    public IReadOnlyList<string> StudioIds { get; set; } = [];
    public string RequestingUserId { get; set; } = string.Empty;
    public bool AllowPrivilegedDefaultCredential { get; set; }
    public bool ActiveSprintOnly { get; set; } = true;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public bool UseSprintDateRange { get; set; }
}

public sealed class StudioJiraTicketResult
{
    public bool IsConfigured { get; set; }
    public string? Message { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsUsingSharedCredential { get; set; }
    public IReadOnlyList<StudioJiraTicketGroup> Groups { get; set; } = [];
}

public sealed class StudioJiraTicketGroup
{
    public string Name { get; set; } = string.Empty;
    public IReadOnlyList<StudioJiraTicket> Tickets { get; set; } = [];
}

public sealed class StudioJiraTicket
{
    public string TicketId { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string AssignedUser { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public IReadOnlyList<string> Components { get; set; } = [];
}

public interface IStudioJiraTicketService
{
    Task<StudioJiraTicketResult> GetTicketsAsync(StudioJiraTicketQuery query, CancellationToken cancellationToken = default);

    async Task<IReadOnlyDictionary<string, StudioJiraTicketResult>> GetTicketsForStudiosAsync(
        StudioJiraTicketBatchQuery query,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, StudioJiraTicketResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var studioId in query.StudioIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            results[studioId] = await GetTicketsAsync(new StudioJiraTicketQuery
            {
                StudioId = studioId,
                RequestingUserId = query.RequestingUserId,
                AllowPrivilegedDefaultCredential = query.AllowPrivilegedDefaultCredential,
                ActiveSprintOnly = query.ActiveSprintOnly,
                StartDate = query.StartDate,
                EndDate = query.EndDate,
                UseSprintDateRange = query.UseSprintDateRange
            }, cancellationToken);
        }
        return results;
    }
}

internal sealed class JiraStudioTicketService(
    HttpClient httpClient,
    IAtlassianConfigurationService configurationService,
    IAtlassianCredentialAccessor credentialAccessor,
    IMemoryCache memoryCache) : IStudioJiraTicketService
{
    public async Task<IReadOnlyDictionary<string, StudioJiraTicketResult>> GetTicketsForStudiosAsync(
        StudioJiraTicketBatchQuery query,
        CancellationToken cancellationToken = default)
    {
        var studioIds = query.StudioIds
            .Where(studioId => !string.IsNullOrWhiteSpace(studioId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var cacheKey = string.Join(
            '|',
            "studio-jira-batch-v1",
            query.RequestingUserId.Trim().ToUpperInvariant(),
            query.AllowPrivilegedDefaultCredential,
            string.Join(',', studioIds.Select(studioId => studioId.ToUpperInvariant())),
            query.ActiveSprintOnly,
            query.UseSprintDateRange,
            query.StartDate?.ToString("yyyy-MM-dd") ?? string.Empty,
            query.EndDate?.ToString("yyyy-MM-dd") ?? string.Empty);
        if (memoryCache.TryGetValue<IReadOnlyDictionary<string, StudioJiraTicketResult>>(cacheKey, out var cached)
            && cached is not null)
        {
            return cached;
        }

        var result = await LoadTicketsForStudiosAsync(query, studioIds, cancellationToken);
        if (result.Values.All(item => item.IsConfigured && string.IsNullOrWhiteSpace(item.Message)))
        {
            memoryCache.Set(cacheKey, result, TimeSpan.FromMinutes(2));
        }
        return result;
    }

    public async Task<StudioJiraTicketResult> GetTicketsAsync(StudioJiraTicketQuery query, CancellationToken cancellationToken = default)
    {
        var cacheKey = string.Join(
            '|',
            "studio-jira-v1",
            query.RequestingUserId.Trim().ToUpperInvariant(),
            query.AllowPrivilegedDefaultCredential,
            query.StudioId.Trim().ToUpperInvariant(),
            query.ActiveSprintOnly,
            query.UseSprintDateRange,
            query.StartDate?.ToString("yyyy-MM-dd") ?? string.Empty,
            query.EndDate?.ToString("yyyy-MM-dd") ?? string.Empty);
        if (memoryCache.TryGetValue<StudioJiraTicketResult>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var result = await LoadTicketsAsync(query, cancellationToken);
        if (result.IsConfigured && string.IsNullOrWhiteSpace(result.Message))
        {
            memoryCache.Set(cacheKey, result, TimeSpan.FromMinutes(2));
        }
        return result;
    }

    private async Task<StudioJiraTicketResult> LoadTicketsAsync(
        StudioJiraTicketQuery query,
        CancellationToken cancellationToken)
    {
        var settings = await configurationService.GetSettingsAsync(cancellationToken);
        if (!settings.JiraEnabled)
        {
            return NotConfigured("Jira integration is disabled. An administrator can enable it in Configuration > Jira & Confluence.");
        }
        if (string.IsNullOrWhiteSpace(settings.JiraBaseUrl))
        {
            return NotConfigured("The Jira base URL is not configured.");
        }

        var mapping = await configurationService.GetStudioMappingAsync(query.StudioId, cancellationToken);
        if (mapping is null)
        {
            return NotConfigured("This studio does not have a Jira mapping. Ask an administrator to configure it.");
        }
        if (mapping.JiraProjectKeys.Count == 0)
        {
            return NotConfigured("No Jira project key is configured for this studio.");
        }
        if (string.IsNullOrWhiteSpace(mapping.JiraStudioComponent))
        {
            return NotConfigured("No studio-identifying Jira component is configured for this studio.");
        }
        if (string.IsNullOrWhiteSpace(query.RequestingUserId))
        {
            return NotConfigured("Sign in and connect your Jira account to view tickets.");
        }

        try
        {
            var resolved = await credentialAccessor.ResolveJiraCredentialAsync(
                query.RequestingUserId,
                query.AllowPrivilegedDefaultCredential,
                cancellationToken);
            if (resolved is not null)
            {
                return await FetchTicketsAsync(settings, mapping, query, resolved, cancellationToken);
            }
        }
        catch (InvalidOperationException exception)
        {
            return NotConfigured(exception.Message);
        }
        return NotConfigured(query.AllowPrivilegedDefaultCredential
            ? "No Jira credential is available. Ask an administrator to grant your privileged account read-only Jira access, or add a personal token."
            : "Your Jira token is not connected. Open My Atlassian Connection and add your Bearer API token.");
    }

    private async Task<IReadOnlyDictionary<string, StudioJiraTicketResult>> LoadTicketsForStudiosAsync(
        StudioJiraTicketBatchQuery query,
        IReadOnlyList<string> studioIds,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, StudioJiraTicketResult>(StringComparer.OrdinalIgnoreCase);
        if (studioIds.Count == 0) return results;

        var settings = await configurationService.GetSettingsAsync(cancellationToken);
        if (!settings.JiraEnabled)
        {
            return CreateBatchMessage(studioIds, "Jira integration is disabled. An administrator can enable it in Configuration > Jira & Confluence.");
        }
        if (string.IsNullOrWhiteSpace(settings.JiraBaseUrl))
        {
            return CreateBatchMessage(studioIds, "The Jira base URL is not configured.");
        }
        if (string.IsNullOrWhiteSpace(query.RequestingUserId))
        {
            return CreateBatchMessage(studioIds, "Sign in and connect your Jira account to view tickets.");
        }

        var mappings = (await configurationService.ListStudioMappingsAsync(cancellationToken))
            .Where(mapping => studioIds.Contains(mapping.StudioId, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(mapping => mapping.StudioId, StringComparer.OrdinalIgnoreCase);
        var validMappings = new List<StudioAtlassianMapping>();
        foreach (var studioId in studioIds)
        {
            if (!mappings.TryGetValue(studioId, out var mapping))
            {
                results[studioId] = NotConfigured("This studio does not have a Jira mapping. Ask an administrator to configure it.");
            }
            else if (mapping.JiraProjectKeys.Count == 0)
            {
                results[studioId] = NotConfigured("No Jira project key is configured for this studio.");
            }
            else if (string.IsNullOrWhiteSpace(mapping.JiraStudioComponent))
            {
                results[studioId] = NotConfigured("No studio-identifying Jira component is configured for this studio.");
            }
            else
            {
                validMappings.Add(mapping);
            }
        }

        AtlassianResolvedCredential? credential;
        try
        {
            credential = await credentialAccessor.ResolveJiraCredentialAsync(
                query.RequestingUserId,
                query.AllowPrivilegedDefaultCredential,
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            foreach (var mapping in validMappings) results[mapping.StudioId] = NotConfigured(exception.Message);
            return results;
        }
        if (credential is null)
        {
            var message = query.AllowPrivilegedDefaultCredential
                ? "No Jira credential is available. Ask an administrator to grant your privileged account read-only Jira access, or add a personal token."
                : "Your Jira token is not connected. Open My Atlassian Connection and add your Bearer API token.";
            foreach (var mapping in validMappings) results[mapping.StudioId] = NotConfigured(message);
            return results;
        }

        foreach (var projectGroup in validMappings.GroupBy(ProjectKeySet, StringComparer.OrdinalIgnoreCase))
        {
            var groupResults = await FetchTicketsForMappingsAsync(
                settings,
                projectGroup.ToList(),
                query,
                credential,
                cancellationToken);
            foreach (var item in groupResults) results[item.Key] = item.Value;
        }
        return results;
    }

    private async Task<IReadOnlyDictionary<string, StudioJiraTicketResult>> FetchTicketsForMappingsAsync(
        AtlassianIntegrationSettings settings,
        IReadOnlyList<StudioAtlassianMapping> mappings,
        StudioJiraTicketBatchQuery query,
        AtlassianResolvedCredential credential,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<long>? sprintIds = null;
        if (query.UseSprintDateRange)
        {
            if (!query.StartDate.HasValue || !query.EndDate.HasValue)
            {
                return CreateBatchResult(
                    mappings,
                    credential,
                    "Select both a start date and an end date to load Jira sprints.");
            }

            var sprintLookup = await FindOverlappingSprintIdsAsync(
                settings,
                mappings[0],
                query.StartDate.Value,
                query.EndDate.Value,
                credential,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(sprintLookup.ErrorMessage))
            {
                return CreateBatchResult(mappings, credential, sprintLookup.ErrorMessage);
            }
            if (sprintLookup.Ids.Count == 0)
            {
                return CreateBatchResult(mappings, credential);
            }
            sprintIds = sprintLookup.Ids;
        }

        var jql = BuildBatchJql(mappings[0].JiraProjectKeys, query, sprintIds);
        var ticketLookup = await FetchAllTicketPagesAsync(settings, jql, credential, cancellationToken);
        if (!string.IsNullOrWhiteSpace(ticketLookup.ErrorMessage))
        {
            return CreateBatchResult(mappings, credential, ticketLookup.ErrorMessage);
        }

        var results = new Dictionary<string, StudioJiraTicketResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in mappings)
        {
            var studioTickets = ticketLookup.Tickets
                .Where(ticket => ticket.Components.Any(component => string.Equals(
                    component,
                    mapping.JiraStudioComponent,
                    StringComparison.OrdinalIgnoreCase)))
                .ToList();
            results[mapping.StudioId] = BuildConfiguredResult(settings, mapping, credential, studioTickets);
        }
        return results;
    }

    private async Task<StudioJiraTicketResult> FetchTicketsAsync(
        AtlassianIntegrationSettings settings,
        StudioAtlassianMapping mapping,
        StudioJiraTicketQuery query,
        AtlassianResolvedCredential credential,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<long>? sprintIds = null;
        if (query.UseSprintDateRange)
        {
            if (!query.StartDate.HasValue || !query.EndDate.HasValue)
            {
                return new StudioJiraTicketResult
                {
                    IsConfigured = true,
                    IsReadOnly = credential.IsReadOnly,
                    IsUsingSharedCredential = credential.IsShared,
                    Message = "Select both a start date and an end date to load Jira sprints."
                };
            }

            var sprintLookup = await FindOverlappingSprintIdsAsync(
                settings,
                mapping,
                query.StartDate.Value,
                query.EndDate.Value,
                credential,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(sprintLookup.ErrorMessage))
            {
                return new StudioJiraTicketResult
                {
                    IsConfigured = true,
                    IsReadOnly = credential.IsReadOnly,
                    IsUsingSharedCredential = credential.IsShared,
                    Message = sprintLookup.ErrorMessage
                };
            }
            if (sprintLookup.Ids.Count == 0)
            {
                return new StudioJiraTicketResult
                {
                    IsConfigured = true,
                    IsReadOnly = credential.IsReadOnly,
                    IsUsingSharedCredential = credential.IsShared
                };
            }
            sprintIds = sprintLookup.Ids;
        }

        var ticketLookup = await FetchAllTicketPagesAsync(
            settings,
            BuildJql(mapping, query, sprintIds),
            credential,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(ticketLookup.ErrorMessage))
        {
            return new StudioJiraTicketResult
            {
                IsConfigured = true,
                IsReadOnly = credential.IsReadOnly,
                IsUsingSharedCredential = credential.IsShared,
                Message = ticketLookup.ErrorMessage
            };
        }

        var tickets = ticketLookup.Tickets
            .Where(ticket => ticket.Components.Any(component => string.Equals(
                component,
                mapping.JiraStudioComponent,
                StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return BuildConfiguredResult(settings, mapping, credential, tickets);
    }

    private async Task<JiraTicketLookupResult> FetchAllTicketPagesAsync(
        AtlassianIntegrationSettings settings,
        string jql,
        AtlassianResolvedCredential credential,
        CancellationToken cancellationToken)
    {
        var tickets = new List<StudioJiraTicket>();
        var pageSize = Math.Clamp(settings.JiraMaxResults, 1, 1000);
        var startAt = 0;
        for (var page = 0; page < 1000; page++)
        {
            var requestUri = BuildSearchUri(settings, jql, startAt, pageSize);
            using var request = CreateJiraRequest(requestUri, credential);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new JiraTicketLookupResult([], BuildJiraRequestError(response, credential, "loading issues"));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var ticketPage = await ParseTicketPageAsync(stream, settings, cancellationToken);
            tickets.AddRange(ticketPage.Tickets);
            if (ticketPage.Tickets.Count == 0) break;

            var nextStartAt = startAt + ticketPage.Tickets.Count;
            if (ticketPage.Total.HasValue
                ? nextStartAt >= ticketPage.Total.Value
                : ticketPage.Tickets.Count < pageSize)
            {
                break;
            }
            startAt = nextStartAt;
        }

        return new JiraTicketLookupResult(tickets);
    }

    private async Task<JiraIdLookupResult> FindOverlappingSprintIdsAsync(
        AtlassianIntegrationSettings settings,
        StudioAtlassianMapping mapping,
        DateOnly startDate,
        DateOnly endDate,
        AtlassianResolvedCredential credential,
        CancellationToken cancellationToken)
    {
        var boardLookup = await FindScrumBoardIdsAsync(settings, mapping, credential, cancellationToken);
        if (!string.IsNullOrWhiteSpace(boardLookup.ErrorMessage)) return boardLookup;

        var sprintIds = new HashSet<long>();
        foreach (var boardId in boardLookup.Ids)
        {
            var startAt = 0;
            for (var page = 0; page < 100; page++)
            {
                var requestUri = $"{settings.JiraBaseUrl.TrimEnd('/')}/rest/agile/1.0/board/{boardId}/sprint?state=active%2Cclosed%2Cfuture&startAt={startAt}&maxResults=50";
                using var request = CreateJiraRequest(requestUri, credential);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound) break;
                if (!response.IsSuccessStatusCode)
                    return new JiraIdLookupResult([], BuildSprintLookupError(response, credential));

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var root = document.RootElement;
                var values = root.TryGetProperty("values", out var valuesElement)
                    && valuesElement.ValueKind == JsonValueKind.Array
                    ? valuesElement
                    : default;
                var valueCount = 0;
                if (values.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sprint in values.EnumerateArray())
                    {
                        valueCount++;
                        if (TryGetLong(sprint, "id", out var sprintId)
                            && TryGetDate(sprint, "startDate", out var sprintStart)
                            && TryGetDate(sprint, "endDate", out var sprintEnd)
                            && sprintStart <= endDate
                            && sprintEnd >= startDate)
                        {
                            sprintIds.Add(sprintId);
                        }
                    }
                }

                var nextStartAt = GetNextStartAt(root, startAt, valueCount);
                if (!nextStartAt.HasValue) break;
                startAt = nextStartAt.Value;
            }
        }

        return new JiraIdLookupResult(sprintIds.Order().ToList());
    }

    private async Task<JiraIdLookupResult> FindScrumBoardIdsAsync(
        AtlassianIntegrationSettings settings,
        StudioAtlassianMapping mapping,
        AtlassianResolvedCredential credential,
        CancellationToken cancellationToken)
    {
        var boardIds = new HashSet<long>();
        foreach (var projectKey in mapping.JiraProjectKeys)
        {
            var startAt = 0;
            for (var page = 0; page < 100; page++)
            {
                var requestUri = $"{settings.JiraBaseUrl.TrimEnd('/')}/rest/agile/1.0/board?projectKeyOrId={Uri.EscapeDataString(projectKey)}&type=scrum&startAt={startAt}&maxResults=50";
                using var request = CreateJiraRequest(requestUri, credential);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return new JiraIdLookupResult([], BuildSprintLookupError(response, credential));

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var root = document.RootElement;
                var values = root.TryGetProperty("values", out var valuesElement)
                    && valuesElement.ValueKind == JsonValueKind.Array
                    ? valuesElement
                    : default;
                var valueCount = 0;
                if (values.ValueKind == JsonValueKind.Array)
                {
                    foreach (var board in values.EnumerateArray())
                    {
                        valueCount++;
                        if (TryGetLong(board, "id", out var boardId)) boardIds.Add(boardId);
                    }
                }

                var nextStartAt = GetNextStartAt(root, startAt, valueCount);
                if (!nextStartAt.HasValue) break;
                startAt = nextStartAt.Value;
            }
        }

        return new JiraIdLookupResult(boardIds.Order().ToList());
    }

    private static HttpRequestMessage CreateJiraRequest(string requestUri, AtlassianResolvedCredential credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Atlassian-Token", "no-check");
        return request;
    }

    private static string BuildSprintLookupError(HttpResponseMessage response, AtlassianResolvedCredential credential) =>
        BuildJiraRequestError(response, credential, "loading sprint dates");

    private static string BuildJiraRequestError(
        HttpResponseMessage response,
        AtlassianResolvedCredential credential,
        string operation) =>
        response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => credential.IsShared
                ? $"Jira rejected the shared read-only token while {operation}. Ask an administrator to update the default Jira credential."
                : $"Jira rejected your personal token while {operation}. Update it from My Atlassian Connection.",
            HttpStatusCode.Forbidden => credential.IsShared
                ? $"The shared Jira account does not have permission to use the Jira Software Agile API while {operation}."
                : $"Your Jira account does not have permission to use the Jira Software Agile API while {operation}.",
            HttpStatusCode.TooManyRequests => $"Jira temporarily limited requests while {operation}. Please try again shortly.",
            _ => $"Jira returned {(int)response.StatusCode} {response.ReasonPhrase} while {operation}. Confirm that the Jira Software Agile API is available."
        };

    private static int? GetNextStartAt(JsonElement root, int currentStartAt, int valueCount)
    {
        if (root.TryGetProperty("isLast", out var isLast)
            && isLast.ValueKind is JsonValueKind.True or JsonValueKind.False
            && isLast.GetBoolean())
        {
            return null;
        }
        if (valueCount == 0) return null;

        var pageSize = root.TryGetProperty("maxResults", out var maxResults)
            && maxResults.TryGetInt32(out var parsedPageSize)
            && parsedPageSize > 0
            ? parsedPageSize
            : valueCount;
        var nextStartAt = currentStartAt + pageSize;
        if (root.TryGetProperty("total", out var total)
            && total.TryGetInt32(out var parsedTotal)
            && nextStartAt >= parsedTotal)
        {
            return null;
        }
        return nextStartAt;
    }

    private static bool TryGetLong(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property)) return false;
        return property.ValueKind == JsonValueKind.Number
            ? property.TryGetInt64(out value)
            : property.ValueKind == JsonValueKind.String
              && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetDate(JsonElement element, string propertyName, out DateOnly value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                property.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var timestamp))
        {
            return false;
        }
        value = DateOnly.FromDateTime(timestamp.DateTime);
        return true;
    }

    private sealed record JiraIdLookupResult(IReadOnlyList<long> Ids, string? ErrorMessage = null);
    private sealed record JiraTicketLookupResult(
        IReadOnlyList<StudioJiraTicket> Tickets,
        string? ErrorMessage = null);
    private sealed record JiraTicketPage(
        IReadOnlyList<StudioJiraTicket> Tickets,
        int? Total);

    private static StudioJiraTicketResult NotConfigured(string message) =>
        new() { IsConfigured = false, Message = message };

    private static IReadOnlyDictionary<string, StudioJiraTicketResult> CreateBatchMessage(
        IReadOnlyList<string> studioIds,
        string message) =>
        studioIds.ToDictionary(
            studioId => studioId,
            _ => NotConfigured(message),
            StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, StudioJiraTicketResult> CreateBatchResult(
        IReadOnlyList<StudioAtlassianMapping> mappings,
        AtlassianResolvedCredential credential,
        string? message = null) =>
        mappings.ToDictionary(
            mapping => mapping.StudioId,
            _ => new StudioJiraTicketResult
            {
                IsConfigured = true,
                IsReadOnly = credential.IsReadOnly,
                IsUsingSharedCredential = credential.IsShared,
                Message = message
            },
            StringComparer.OrdinalIgnoreCase);

    private static StudioJiraTicketResult BuildConfiguredResult(
        AtlassianIntegrationSettings settings,
        StudioAtlassianMapping mapping,
        AtlassianResolvedCredential credential,
        IReadOnlyList<StudioJiraTicket> tickets) =>
        new()
        {
            IsConfigured = true,
            IsReadOnly = credential.IsReadOnly,
            IsUsingSharedCredential = credential.IsShared,
            Groups =
            [
                new StudioJiraTicketGroup
                {
                    Name = "Direct Support",
                    Tickets = tickets.Where(ticket => IsSupportRequest(ticket, settings, mapping)).ToList()
                },
                new StudioJiraTicketGroup
                {
                    Name = "Indirect Support",
                    Tickets = tickets.Where(ticket => !IsSupportRequest(ticket, settings, mapping)).ToList()
                }
            ]
        };

    private static string ProjectKeySet(StudioAtlassianMapping mapping) =>
        string.Join(
            '\u001f',
            mapping.JiraProjectKeys
                .Select(key => key.Trim().ToUpperInvariant())
                .Order(StringComparer.OrdinalIgnoreCase));

    private static string BuildSearchUri(
        AtlassianIntegrationSettings settings,
        string jql,
        int startAt,
        int maxResults)
    {
        var fields = Uri.EscapeDataString("summary,assignee,status,priority,components");
        return $"{settings.JiraBaseUrl.TrimEnd('/')}{settings.JiraSearchApiPath}?jql={Uri.EscapeDataString(jql)}&fields={fields}&startAt={startAt}&maxResults={maxResults}";
    }

    internal static string BuildJql(
        StudioAtlassianMapping mapping,
        StudioJiraTicketQuery query,
        IReadOnlyList<long>? sprintIds = null)
    {
        var projectClause = mapping.JiraProjectKeys.Count == 1
            ? $"project = {QuoteJql(mapping.JiraProjectKeys[0])}"
            : $"project in ({string.Join(", ", mapping.JiraProjectKeys.Select(QuoteJql))})";
        var clauses = new List<string>
        {
            projectClause,
            $"component = {QuoteJql(mapping.JiraStudioComponent)}",
            $"issuetype in ({QuoteJql("Task")}, {QuoteJql("Story")})"
        };
        if (query.UseSprintDateRange)
        {
            if (sprintIds is null || sprintIds.Count == 0)
                throw new InvalidOperationException("Sprint IDs are required for sprint date-range filtering.");
            clauses.Add($"sprint in ({string.Join(", ", sprintIds.Distinct().Order())})");
        }
        else
        {
            if (query.ActiveSprintOnly) clauses.Add("sprint in openSprints()");
            if (query.StartDate.HasValue) clauses.Add($"created >= {QuoteJql(query.StartDate.Value.ToString("yyyy-MM-dd"))}");
            if (query.EndDate.HasValue) clauses.Add($"created <= {QuoteJql(query.EndDate.Value.ToString("yyyy-MM-dd"))}");
        }
        return string.Join(" AND ", clauses) + " ORDER BY priority DESC, updated DESC";
    }

    private static string BuildBatchJql(
        IReadOnlyList<string> projectKeys,
        StudioJiraTicketBatchQuery query,
        IReadOnlyList<long>? sprintIds)
    {
        var projectClause = projectKeys.Count == 1
            ? $"project = {QuoteJql(projectKeys[0])}"
            : $"project in ({string.Join(", ", projectKeys.Select(QuoteJql))})";
        var clauses = new List<string>
        {
            projectClause,
            $"issuetype in ({QuoteJql("Task")}, {QuoteJql("Story")})"
        };
        if (query.UseSprintDateRange)
        {
            if (sprintIds is null || sprintIds.Count == 0)
                throw new InvalidOperationException("Sprint IDs are required for sprint date-range filtering.");
            clauses.Add($"sprint in ({string.Join(", ", sprintIds.Distinct().Order())})");
        }
        else
        {
            if (query.ActiveSprintOnly) clauses.Add("sprint in openSprints()");
            if (query.StartDate.HasValue) clauses.Add($"created >= {QuoteJql(query.StartDate.Value.ToString("yyyy-MM-dd"))}");
            if (query.EndDate.HasValue) clauses.Add($"created <= {QuoteJql(query.EndDate.Value.ToString("yyyy-MM-dd"))}");
        }
        return string.Join(" AND ", clauses) + " ORDER BY priority DESC, updated DESC";
    }

    private static string QuoteJql(string value) => JsonSerializer.Serialize(value);

    private static async Task<JiraTicketPage> ParseTicketPageAsync(
        Stream stream,
        AtlassianIntegrationSettings settings,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
        {
            return new JiraTicketPage([], 0);
        }

        var tickets = new List<StudioJiraTicket>();
        foreach (var issue in issues.EnumerateArray())
        {
            var key = GetString(issue, "key");
            var fields = issue.TryGetProperty("fields", out var fieldsElement) ? fieldsElement : default;
            var components = GetComponentNames(fields);
            tickets.Add(new StudioJiraTicket
            {
                TicketId = key,
                Summary = GetString(fields, "summary"),
                AssignedUser = GetNestedString(fields, "assignee", "displayName", "Unassigned"),
                Status = GetNestedString(fields, "status", "name", string.Empty),
                Priority = GetNestedString(fields, "priority", "name", string.Empty),
                Url = $"{settings.JiraBaseUrl.TrimEnd('/')}/browse/{key}",
                Components = components
            });
        }

        int? total = document.RootElement.TryGetProperty("total", out var totalElement)
            && totalElement.TryGetInt32(out var parsedTotal)
                ? parsedTotal
                : null;
        return new JiraTicketPage(tickets, total);
    }

    private static bool IsSupportRequest(StudioJiraTicket ticket, AtlassianIntegrationSettings settings, StudioAtlassianMapping mapping)
    {
        var supportComponent = string.IsNullOrWhiteSpace(mapping.JiraSupportComponent)
            ? settings.JiraDefaultSupportComponent
            : mapping.JiraSupportComponent;
        return ticket.Components.Any(component => string.Equals(component, supportComponent, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> GetComponentNames(JsonElement fields)
    {
        if (fields.ValueKind == JsonValueKind.Undefined
            || !fields.TryGetProperty("components", out var components)
            || components.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return components.EnumerateArray()
            .Select(component => GetString(component, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
    }

    private static string GetNestedString(JsonElement element, string propertyName, string nestedPropertyName, string fallback)
    {
        if (element.ValueKind == JsonValueKind.Undefined
            || !element.TryGetProperty(propertyName, out var nested)
            || nested.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }
        return GetString(nested, nestedPropertyName, fallback);
    }

    private static string GetString(JsonElement element, string propertyName, string fallback = "")
    {
        if (element.ValueKind == JsonValueKind.Undefined
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }
        return value.GetString() ?? fallback;
    }
}
