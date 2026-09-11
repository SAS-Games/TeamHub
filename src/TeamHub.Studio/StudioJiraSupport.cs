using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TeamHub.Studio;

public sealed class StudioJiraTicketQuery
{
    public string StudioId { get; set; } = string.Empty;
    public string RequestingUserId { get; set; } = string.Empty;
    public bool ActiveSprintOnly { get; set; } = true;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
}

public sealed class StudioJiraTicketResult
{
    public bool IsConfigured { get; set; }
    public string? Message { get; set; }
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
    IAtlassianCredentialAccessor credentialAccessor) : IStudioJiraTicketService
{
    public async Task<StudioJiraTicketResult> GetTicketsAsync(StudioJiraTicketQuery query, CancellationToken cancellationToken = default)
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

        string? token;
        try
        {
            token = await credentialAccessor.GetJiraTokenAsync(query.RequestingUserId, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return NotConfigured(exception.Message);
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            return NotConfigured("Your Jira token is not connected. Open My Atlassian Connection and add your Bearer API token.");
        }

        var requestUri = BuildSearchUri(settings, mapping, query);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Atlassian-Token", "no-check");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "Jira rejected your personal token. Update it from My Atlassian Connection and confirm that your Jira account can access this project."
                : $"Jira returned {(int)response.StatusCode} {response.ReasonPhrase}. Check the Jira URL and studio mapping.";
            return new StudioJiraTicketResult { IsConfigured = true, Message = message };
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var tickets = await ParseTicketsAsync(stream, settings, mapping, cancellationToken);
        return new StudioJiraTicketResult
        {
            IsConfigured = true,
            Groups =
            [
                new StudioJiraTicketGroup { Name = "Direct Support", Tickets = tickets.Where(ticket => IsSupportRequest(ticket, settings, mapping)).ToList() },
                new StudioJiraTicketGroup { Name = "Indirect Support", Tickets = tickets.Where(ticket => !IsSupportRequest(ticket, settings, mapping)).ToList() }
            ]
        };
    }

    private static StudioJiraTicketResult NotConfigured(string message) =>
        new() { IsConfigured = false, Message = message };

    private static string BuildSearchUri(AtlassianIntegrationSettings settings, StudioAtlassianMapping mapping, StudioJiraTicketQuery query)
    {
        var jql = BuildJql(mapping, query);
        var fields = Uri.EscapeDataString("summary,assignee,status,priority,components");
        return $"{settings.JiraBaseUrl.TrimEnd('/')}{settings.JiraSearchApiPath}?jql={Uri.EscapeDataString(jql)}&fields={fields}&maxResults={settings.JiraMaxResults}";
    }

    private static string BuildJql(StudioAtlassianMapping mapping, StudioJiraTicketQuery query)
    {
        var projectClause = mapping.JiraProjectKeys.Count == 1
            ? $"project = {QuoteJql(mapping.JiraProjectKeys[0])}"
            : $"project in ({string.Join(", ", mapping.JiraProjectKeys.Select(QuoteJql))})";
        var clauses = new List<string>
        {
            projectClause,
            $"component = {QuoteJql(mapping.JiraStudioComponent)}"
        };
        if (query.ActiveSprintOnly) clauses.Add("sprint in openSprints()");
        if (query.StartDate.HasValue) clauses.Add($"created >= {QuoteJql(query.StartDate.Value.ToString("yyyy-MM-dd"))}");
        if (query.EndDate.HasValue) clauses.Add($"created <= {QuoteJql(query.EndDate.Value.ToString("yyyy-MM-dd"))}");
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
