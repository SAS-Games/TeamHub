using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace TeamHub.Studio;

public sealed class StudioJiraConfigurationOptions
{
    public const string SectionName = "StudioJiraConfiguration";
    public string ConfigPath { get; set; } = string.Empty;
}

public sealed class StudioJiraSettings
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string SearchApiPath { get; set; } = "/rest/api/3/search";
    public string AuthMode { get; set; } = "Basic";
    public string Username { get; set; } = string.Empty;
    public string ApiTokenEnvironmentVariable { get; set; } = "TEAMHUB_JIRA_API_TOKEN";
    public int MaxResults { get; set; } = 100;
    public string DefaultSupportComponent { get; set; } = "studio_Support";
    public List<StudioJiraMapping> StudioMappings { get; set; } = [];
}

public sealed class StudioJiraMapping
{
    public string StudioProjectName { get; set; } = string.Empty;
    public string JiraProjectKey { get; set; } = string.Empty;
    public string StudioComponent { get; set; } = string.Empty;
    public string? SupportComponent { get; set; }
}

public sealed class StudioJiraTicketQuery
{
    public string StudioProjectName { get; set; } = string.Empty;
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

internal sealed class JiraStudioTicketService(HttpClient httpClient, IConfiguration configuration) : IStudioJiraTicketService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<StudioJiraTicketResult> GetTicketsAsync(StudioJiraTicketQuery query, CancellationToken cancellationToken = default)
    {
        var settingsResult = await LoadSettingsAsync(cancellationToken);
        if (!settingsResult.IsConfigured || settingsResult.Settings is null)
        {
            return new StudioJiraTicketResult { IsConfigured = false, Message = settingsResult.Message };
        }

        var settings = settingsResult.Settings;
        var mapping = settings.StudioMappings.FirstOrDefault(item => string.Equals(item.StudioProjectName, query.StudioProjectName, StringComparison.OrdinalIgnoreCase));
        if (mapping is null)
        {
            return new StudioJiraTicketResult
            {
                IsConfigured = false,
                Message = $"No Jira mapping was found for studio project '{query.StudioProjectName}'. Add it to the Studio Jira configuration file."
            };
        }

        var authMessage = ConfigureAuthentication(settings);
        if (authMessage is not null)
        {
            return new StudioJiraTicketResult { IsConfigured = false, Message = authMessage };
        }

        var requestUri = BuildSearchUri(settings, mapping, query);
        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new StudioJiraTicketResult
            {
                IsConfigured = true,
                Message = $"Jira returned {(int)response.StatusCode} {response.ReasonPhrase}. Check Jira URL, credentials, and project/component mapping."
            };
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var tickets = await ParseTicketsAsync(stream, settings, mapping, cancellationToken);
        return new StudioJiraTicketResult
        {
            IsConfigured = true,
            Groups =
            [
                new StudioJiraTicketGroup { Name = "Support Request", Tickets = tickets.Where(ticket => IsSupportRequest(ticket, settings, mapping)).ToList() },
                new StudioJiraTicketGroup { Name = "Indirect Support", Tickets = tickets.Where(ticket => !IsSupportRequest(ticket, settings, mapping)).ToList() }
            ]
        };
    }

    private async Task<(bool IsConfigured, StudioJiraSettings? Settings, string? Message)> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var configPath = configuration.GetSection(StudioJiraConfigurationOptions.SectionName).Get<StudioJiraConfigurationOptions>()?.ConfigPath;
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return (false, null, "Studio Jira configuration path is not configured.");
        }

        if (!File.Exists(configPath))
        {
            return (false, null, $"Studio Jira configuration file was not found: {configPath}");
        }

        await using var stream = File.OpenRead(configPath);
        var settings = await JsonSerializer.DeserializeAsync<StudioJiraSettings>(stream, SerializerOptions, cancellationToken);
        if (settings is null || !settings.Enabled)
        {
            return (false, settings, "Studio Jira integration is disabled. Enable it in the Studio Jira configuration file after setting Jira URL, project mapping, and credentials.");
        }

        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return (false, settings, "Jira baseUrl is missing in the Studio Jira configuration file.");
        }

        if (settings.StudioMappings.Count == 0)
        {
            return (false, settings, "No studio-to-Jira mappings are configured.");
        }

        return (true, settings, null);
    }

    private string? ConfigureAuthentication(StudioJiraSettings settings)
    {
        var token = Environment.GetEnvironmentVariable(settings.ApiTokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(token))
        {
            return $"Jira token environment variable '{settings.ApiTokenEnvironmentVariable}' is not set.";
        }

        if (string.Equals(settings.AuthMode, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.Username))
        {
            return "Jira username is required for Basic authentication.";
        }

        var basicToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{token}"));
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicToken);
        return null;
    }

    private static string BuildSearchUri(StudioJiraSettings settings, StudioJiraMapping mapping, StudioJiraTicketQuery query)
    {
        var baseUrl = settings.BaseUrl.TrimEnd('/');
        var searchPath = string.IsNullOrWhiteSpace(settings.SearchApiPath) ? "/rest/api/3/search" : settings.SearchApiPath;
        var jql = BuildJql(mapping, query);
        var maxResults = settings.MaxResults <= 0 ? 100 : settings.MaxResults;
        var fields = Uri.EscapeDataString("summary,assignee,status,priority,components");
        return $"{baseUrl}{searchPath}?jql={Uri.EscapeDataString(jql)}&fields={fields}&maxResults={maxResults}";
    }

    private static string BuildJql(StudioJiraMapping mapping, StudioJiraTicketQuery query)
    {
        var clauses = new List<string>
        {
            $"project = {QuoteJql(mapping.JiraProjectKey)}",
            $"component = {QuoteJql(mapping.StudioComponent)}"
        };

        if (query.ActiveSprintOnly)
        {
            clauses.Add("sprint in openSprints()");
        }

        if (query.StartDate.HasValue)
        {
            clauses.Add($"created >= {QuoteJql(query.StartDate.Value.ToString("yyyy-MM-dd"))}");
        }

        if (query.EndDate.HasValue)
        {
            clauses.Add($"created <= {QuoteJql(query.EndDate.Value.ToString("yyyy-MM-dd"))}");
        }

        return string.Join(" AND ", clauses) + " ORDER BY priority DESC, updated DESC";
    }

    private static string QuoteJql(string value)
        => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private static async Task<List<StudioJiraTicket>> ParseTicketsAsync(Stream stream, StudioJiraSettings settings, StudioJiraMapping mapping, CancellationToken cancellationToken)
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
                Url = $"{settings.BaseUrl.TrimEnd('/')}/browse/{key}",
                Components = components
            });
        }

        return tickets
            .Where(ticket => ticket.Components.Any(component => string.Equals(component, mapping.StudioComponent, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static bool IsSupportRequest(StudioJiraTicket ticket, StudioJiraSettings settings, StudioJiraMapping mapping)
    {
        var supportComponent = string.IsNullOrWhiteSpace(mapping.SupportComponent) ? settings.DefaultSupportComponent : mapping.SupportComponent;
        return ticket.Components.Any(component => string.Equals(component, supportComponent, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> GetComponentNames(JsonElement fields)
    {
        if (fields.ValueKind == JsonValueKind.Undefined || !fields.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Array)
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
        if (element.ValueKind == JsonValueKind.Undefined || !element.TryGetProperty(propertyName, out var nested) || nested.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        return GetString(nested, nestedPropertyName, fallback);
    }

    private static string GetString(JsonElement element, string propertyName, string fallback = "")
    {
        if (element.ValueKind == JsonValueKind.Undefined || !element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        return value.GetString() ?? fallback;
    }
}