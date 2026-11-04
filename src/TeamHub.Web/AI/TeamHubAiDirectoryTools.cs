using System.Text.Json;
using TeamHub.AI.Chat.Contracts;
using TeamHub.Authentication;
using TeamHub.Studio;
using TeamHub.Team;
using TeamHub.Web.AccessControl;

namespace TeamHub.Web.AI;

/// <summary>
/// Read-only directory tools available to TeamHub AI Chat. This is an internal
/// application service, not a public HTTP API.
/// </summary>
public sealed class TeamHubAiDirectoryTools(
    ICurrentAccessService access,
    ITeamDirectoryService teamDirectory,
    IStudioDirectoryService studios,
    ILogger<TeamHubAiDirectoryTools> logger) : IAiChatToolProvider
{
    private const int MaximumTeamMembers = 100;
    private const int MaximumStudios = 60;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<AiChatToolDefinition> Definitions { get; } =
    [
        new("list_team_members",
            "List authorized TeamHub team members. Use for requests about all members or members matching a role, section, name, GID, or email.",
            "{\"query\":\"optional name, role, section, GID, or email\"}"),
        new("get_team_member",
            "Get detailed information for one TeamHub team member. Multiple possible matches are returned when the query is ambiguous.",
            "{\"query\":\"required name, GID, or email\"}"),
        new("list_team_specializations",
            "List authorized team responsibility pods, focus areas, and recorded members.",
            "{\"query\":\"optional pod, focus area, or member\"}"),
        new("list_studios",
            "List active studios and projects visible to the signed-in user.",
            "{\"query\":\"optional studio, project, group, or location\"}"),
        new("get_studio_details",
            "Get details for a specific active studio, including project, contacts, members, tools, and configured links.",
            "{\"studio\":\"required studio ID, studio name, or project name\"}"),
        new("list_studio_members",
            "List the configured members, roles, responsibilities, and email addresses for a specific active studio.",
            "{\"studio\":\"required studio ID, studio name, or project name\"}")
    ];

    public async Task<AiChatSource> ExecuteAsync(
        AiChatUserContext user,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current.User is null
            || !string.Equals(current.User.UserId, user.UserId, StringComparison.OrdinalIgnoreCase))
            throw new AiChatException("The signed-in user could not be verified for the requested TeamHub tool.");

        toolName = toolName?.Trim() ?? string.Empty;
        var isTeamTool = toolName is "list_team_members" or "get_team_member" or "list_team_specializations";
        var isStudioTool = toolName is "list_studios" or "get_studio_details" or "list_studio_members";
        if (!isTeamTool && !isStudioTool)
            throw new AiChatException($"The TeamHub tool '{toolName}' is not available.");
        if (isTeamTool && !await access.CanAsync(TeamHubModules.Team, AccessLevel.ReadOnly, cancellationToken))
            throw new AiChatException("You do not have access to the TeamHub team directory.");
        if (isStudioTool
            && !await access.CanAsync(TeamHubModules.StudioConfiguration, AccessLevel.ReadOnly, cancellationToken))
            throw new AiChatException("You do not have access to TeamHub studio information.");

        logger.LogInformation("TeamHub AI tool {ToolName} executed for {UserId}", toolName, user.UserId);
        return toolName switch
        {
            "list_team_members" => await ListTeamMembersAsync(arguments, cancellationToken),
            "get_team_member" => await GetTeamMemberAsync(arguments, cancellationToken),
            "list_team_specializations" => await ListTeamSpecializationsAsync(arguments, cancellationToken),
            "list_studios" => await ListStudiosAsync(arguments, cancellationToken),
            "get_studio_details" => await GetStudioDetailsAsync(arguments, cancellationToken),
            "list_studio_members" => await ListStudioMembersAsync(arguments, cancellationToken),
            _ => throw new AiChatException($"The TeamHub tool '{toolName}' is not available.")
        };
    }

    private async Task<AiChatSource> ListTeamMembersAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var query = OptionalArgument(arguments, "query");
        var all = (await teamDirectory.GetTeamDirectoryAsync(cancellationToken)).Members;
        var matching = Filter(all, query, member =>
                $"{member.EmployeeName} {member.Role} {member.Gid} {member.Email} {member.Section}")
            .OrderBy(member => member.EmployeeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Source("list_team_members", "TeamHub team members", new
        {
            query,
            totalMatches = matching.Count,
            truncated = matching.Count > MaximumTeamMembers,
            members = matching.Take(MaximumTeamMembers).Select(member => new
            {
                member.EmployeeName,
                member.Role,
                member.Gid,
                member.Email,
                Section = TeamMemberSections.Normalize(member.Section)
            })
        });
    }

    private async Task<AiChatSource> GetTeamMemberAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var query = RequiredArgument(arguments, "query");
        var matching = Filter(
                (await teamDirectory.GetTeamDirectoryAsync(cancellationToken)).Members,
                query,
                member => $"{member.EmployeeName} {member.Gid} {member.Email}")
            .OrderByDescending(member => IsExactMemberMatch(member, query))
            .ThenBy(member => member.EmployeeName, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(member => new
            {
                member.EmployeeName,
                member.Role,
                member.Gid,
                member.Email,
                member.ContactNumber,
                Section = TeamMemberSections.Normalize(member.Section)
            })
            .ToList();
        return Source("get_team_member", "TeamHub team member details", new
        {
            query,
            matchCount = matching.Count,
            members = matching
        });
    }

    private async Task<AiChatSource> ListTeamSpecializationsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var query = OptionalArgument(arguments, "query");
        var matching = Filter(
                (await teamDirectory.GetTeamDirectoryAsync(cancellationToken)).Specializations,
                query,
                item => $"{item.Pod} {item.FocusAreas} {item.Members}")
            .OrderBy(item => item.Pod, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .Select(item => new { item.Pod, item.FocusAreas, item.Members });
        return Source("list_team_specializations", "TeamHub team specializations", new { query, specializations = matching });
    }

    private async Task<AiChatSource> ListStudiosAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var query = OptionalArgument(arguments, "query");
        var matching = Filter(
                (await studios.GetStudiosAsync(cancellationToken)).Where(studio => studio.IsActive),
                query,
                studio => $"{studio.Id} {studio.StudioName} {studio.ProjectName} {studio.StudioGroup} {studio.Location}")
            .OrderBy(studio => studio.StudioName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Source("list_studios", "Active TeamHub studios", new
        {
            query,
            totalMatches = matching.Count,
            truncated = matching.Count > MaximumStudios,
            studios = matching.Take(MaximumStudios).Select(studio => new
            {
                studio.Id,
                studio.StudioName,
                studio.ProjectName,
                studio.StudioGroup,
                studio.Location,
                studio.TimeZoneId
            })
        });
    }

    private async Task<AiChatSource> GetStudioDetailsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var query = RequiredArgument(arguments, "studio");
        var studio = ResolveStudio(await studios.GetStudiosAsync(cancellationToken), query);
        return Source("get_studio_details", $"TeamHub studio: {studio.StudioName}", new
        {
            studio.Id,
            studio.StudioName,
            studio.ProjectName,
            studio.AboutGame,
            studio.StudioGroup,
            studio.Location,
            studio.TimeZoneId,
            ourContacts = studio.OurContacts.Select(contact => new { contact.Name, contact.Role }),
            teamMembers = studio.TeamMembers.Select(member => new
                { member.Name, member.RolesAndResponsibilities, member.EmailId }),
            developmentTools = studio.DevelopmentTools.Select(tool => new { tool.Name, tool.Description }),
            importantLinks = studio.ImportantLinks.Select(link => new { link.Label, link.Url, link.Description })
        });
    }

    private async Task<AiChatSource> ListStudioMembersAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var query = RequiredArgument(arguments, "studio");
        var studio = ResolveStudio(await studios.GetStudiosAsync(cancellationToken), query);
        return Source("list_studio_members", $"TeamHub studio members: {studio.StudioName}", new
        {
            studio.Id,
            studio.StudioName,
            studio.ProjectName,
            members = studio.TeamMembers
                .OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
                .Select(member => new { member.Name, member.RolesAndResponsibilities, member.EmailId })
        });
    }

    private static StudioDetails ResolveStudio(IEnumerable<StudioDetails> values, string query)
    {
        var active = values.Where(studio => studio.IsActive).ToList();
        var exact = active.Where(studio =>
                string.Equals(studio.Id, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(studio.StudioName, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(studio.ProjectName, query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count == 1) return exact[0];
        var partial = active.Where(studio =>
                studio.StudioName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || studio.ProjectName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (partial.Count == 1) return partial[0];
        if (exact.Count == 0 && partial.Count == 0)
            throw new AiChatException($"No active studio matched '{query}'.");
        var names = string.Join(", ", (exact.Count > 0 ? exact : partial).Select(studio => studio.StudioName));
        throw new AiChatException($"Studio '{query}' is ambiguous. Matching studios: {names}.");
    }

    private static IEnumerable<T> Filter<T>(IEnumerable<T> values, string? query, Func<T, string> searchable)
    {
        if (string.IsNullOrWhiteSpace(query)) return values;
        return values.Where(value => searchable(value).Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExactMemberMatch(TeamMemberDto member, string query) =>
        string.Equals(member.EmployeeName, query, StringComparison.OrdinalIgnoreCase)
        || string.Equals(member.Gid, query, StringComparison.OrdinalIgnoreCase)
        || string.Equals(member.Email, query, StringComparison.OrdinalIgnoreCase);

    private static string? OptionalArgument(JsonElement arguments, string name)
    {
        EnsureObject(arguments);
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new AiChatException($"Tool argument '{name}' must be text.");
        var result = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(result)) return null;
        if (result.Length > 200) throw new AiChatException($"Tool argument '{name}' is too long.");
        return result;
    }

    private static string RequiredArgument(JsonElement arguments, string name) =>
        OptionalArgument(arguments, name)
        ?? throw new AiChatException($"Tool argument '{name}' is required.");

    private static void EnsureObject(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new AiChatException("TeamHub tool arguments must be a JSON object.");
    }

    private static AiChatSource Source(string toolName, string title, object value) =>
        new($"teamhub-tool-{toolName}", title, JsonSerializer.Serialize(value, JsonOptions));
}
