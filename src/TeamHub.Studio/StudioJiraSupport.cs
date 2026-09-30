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
}

internal sealed class JiraStudioTicketService(
    HttpClient httpClient,
    IAtlassianConfigurationService configurationService,
    IAtlassianCredentialAccessor credentialAccessor,
    IMemoryCache memoryCache) : IStudioJiraTicketService
{
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

        var requestUri = BuildSearchUri(settings, mapping, query, sprintIds);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Atlassian-Token", "no-check");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? credential.IsShared
                    ? "Jira rejected the shared read-only token. Ask an administrator to update the default Jira credential."
                    : "Jira rejected your personal token. Update it from My Atlassian Connection and confirm that your Jira account can access this project."
                : $"Jira returned {(int)response.StatusCode} {response.ReasonPhrase}. Check the Jira URL and studio mapping.";
            return new StudioJiraTicketResult
            {
                IsConfigured = true,
                IsReadOnly = credential.IsReadOnly,
                IsUsingSharedCredential = credential.IsShared,
                Message = message
            };
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var tickets = await ParseTicketsAsync(stream, settings, mapping, cancellationToken);
        return new StudioJiraTicketResult
        {
            IsConfigured = true,
            IsReadOnly = credential.IsReadOnly,
            IsUsingSharedCredential = credential.IsShared,
            Groups =
            [
                new StudioJiraTicketGroup { Name = "Direct Support", Tickets = tickets.Where(ticket => IsSupportRequest(ticket, settings, mapping)).ToList() },
                new StudioJiraTicketGroup { Name = "Indirect Support", Tickets = tickets.Where(ticket => !IsSupportRequest(ticket, settings, mapping)).ToList() }
            ]
        };
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
        response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? credential.IsShared
                ? "Jira rejected the shared read-only token while loading sprint dates. Ask an administrator to update the default Jira credential."
                : "Jira rejected your personal token while loading sprint dates. Update it from My Atlassian Connection."
            : $"Jira returned {(int)response.StatusCode} {response.ReasonPhrase} while loading sprint dates. Confirm that the Jira Software Agile API is available.";

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

    private static StudioJiraTicketResult NotConfigured(string message) =>
        new() { IsConfigured = false, Message = message };

    private static string BuildSearchUri(
        AtlassianIntegrationSettings settings,
        StudioAtlassianMapping mapping,
        StudioJiraTicketQuery query,
        IReadOnlyList<long>? sprintIds = null)
    {
        var jql = BuildJql(mapping, query, sprintIds);
        var fields = Uri.EscapeDataString("summary,assignee,status,priority,components");
        return $"{settings.JiraBaseUrl.TrimEnd('/')}{settings.JiraSearchApiPath}?jql={Uri.EscapeDataString(jql)}&fields={fields}&maxResults={settings.JiraMaxResults}";
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

    private static string QuoteJql(string value) => JsonSerializer.Serialize(value);

    private static async Task<List<StudioJiraTicket>> ParseTicketsAsync(
        Stream stream,
        AtlassianIntegrationSettings settings,
        StudioAtlassianMapping mapping,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
        {
            return [];
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

        return tickets
            .Where(ticket => ticket.Components.Any(component => string.Equals(component, mapping.JiraStudioComponent, StringComparison.OrdinalIgnoreCase)))
            .ToList();
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
