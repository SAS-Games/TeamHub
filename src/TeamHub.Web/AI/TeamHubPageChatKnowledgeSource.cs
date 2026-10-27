using System.Text.Json;
using TeamHub.AI.Chat.Contracts;
using TeamHub.Authentication;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.Notifications.Services;
using TeamHub.Team;
using TeamHub.Web.AccessControl;
using TeamHub.Web.Home;

namespace TeamHub.Web.AI;

/// <summary>
/// Makes the remaining read-only TeamHub page content available to AI Chat
/// through the same user and module permissions as the UI.
/// </summary>
public sealed class TeamHubPageChatKnowledgeSource(
    ICurrentAccessService access,
    IHomeContentService home,
    ITeamAchievementService achievements,
    ICustomTeamTabService customTabs,
    IFlowService flows,
    IFlowPublicationWorkflowService publications,
    IWorkflowNotificationInbox notifications) : IAiChatKnowledgeSource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<IReadOnlyList<AiChatSource>> SearchAsync(
        AiChatUserContext user,
        string question,
        CancellationToken cancellationToken = default)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current.User is null
            || !string.Equals(current.User.UserId, user.UserId, StringComparison.OrdinalIgnoreCase))
            throw new AiChatException("The signed-in user could not be verified.");

        var result = new List<AiChatSource>();
        if (await access.CanAsync(TeamHubModules.Home, AccessLevel.ReadOnly, cancellationToken))
            await AddHomeAsync(result, question, cancellationToken);
        if (await access.CanAsync(TeamHubModules.Team, AccessLevel.ReadOnly, cancellationToken))
            await AddTeamPagesAsync(result, question, cancellationToken);
        if (await access.CanAsync(TeamHubModules.FlowDesigner, AccessLevel.ReadOnly, cancellationToken))
            await AddFlowsAsync(result, question, cancellationToken);
        if (await access.CanAsync(TeamHubModules.Notifications, AccessLevel.ReadOnly, cancellationToken))
            await AddNotificationsAsync(result, user.UserId, question, cancellationToken);
        return result;
    }

    private async Task AddHomeAsync(
        ICollection<AiChatSource> sources,
        string question,
        CancellationToken cancellationToken)
    {
        var content = await home.GetContentAsync(cancellationToken);
        var searchable = string.Join(' ', new[]
        {
            "home vision mission about project highlights useful links",
            content.ProjectName, content.Tagline, content.Vision, content.Mission, content.Description,
            string.Join(' ', content.Highlights.Select(item => $"{item.Title} {item.Detail}")),
            string.Join(' ', content.UsefulLinks.Select(item => $"{item.Label} {item.Description}"))
        });
        if (!Matches(question, searchable)) return;

        var canViewLinks = await access.CanAsync(TeamHubModules.UsefulLinks, AccessLevel.ReadOnly, cancellationToken);
        AddJsonSource(sources, "teamhub-home", "TeamHub Home page", new
        {
            content.ProjectName,
            content.Tagline,
            content.Vision,
            content.Mission,
            content.Description,
            content.Highlights,
            UsefulLinks = canViewLinks ? content.UsefulLinks : []
        });
    }

    private async Task AddTeamPagesAsync(
        ICollection<AiChatSource> sources,
        string question,
        CancellationToken cancellationToken)
    {
        var achievementItems = await achievements.GetAchievementsAsync(cancellationToken);
        var matchingAchievements = SelectRelevant(
            achievementItems,
            question,
            item => $"team achievement achievements {item.Title} {item.Description} {item.Impact} {item.AchievedBy}",
            100).Select(item => new
            {
                item.Title,
                item.Description,
                item.Impact,
                item.AchievedBy,
                item.AchievedOn
            });
        AddJsonSource(sources, "teamhub-achievements", "Team achievements", matchingAchievements);

        var tabs = await customTabs.ListTabsAsync(cancellationToken);
        var rows = tabs.SelectMany(tab => tab.Tables.SelectMany(table => table.Rows.Select(row => new
        {
            Tab = tab.Name,
            Table = table.Name,
            Columns = table.Columns.ToDictionary(column => column.Key, column => column.Label),
            Values = row.Values.ToDictionary(item => item.Key, item => Clip(item.Value, 300)),
            row.SourceStatus,
            Searchable = $"team custom tab {tab.Name} {table.Name} "
                + string.Join(' ', table.Columns.Select(column => column.Label)) + " "
                + string.Join(' ', row.Values.Values)
        })));
        var matchingRows = SelectRelevant(rows, question, item => item.Searchable, 25)
            .Select(item => new
            {
                item.Tab,
                item.Table,
                item.Columns,
                item.Values,
                item.SourceStatus
            });
        AddJsonSource(sources, "teamhub-custom-tabs", "TeamHub custom tab data", matchingRows);
    }

    private async Task AddFlowsAsync(
        ICollection<AiChatSource> sources,
        string question,
        CancellationToken cancellationToken)
    {
        var accessible = await flows.ListAsync(cancellationToken);
        var matching = SelectRelevant(accessible, question,
                item => $"flow designer diagram process {item.Name} {item.Description} {item.DiagramType}", 100)
            .ToList();
        AddJsonSource(sources, "teamhub-flow-designer", "Accessible Flow Designer diagrams", matching);

        var requestedDefinitions = matching
            .Where(item => question.Contains(item.Name, StringComparison.OrdinalIgnoreCase))
            .Take(3);
        foreach (var summary in requestedDefinitions)
        {
            var definition = await flows.GetAsync(summary.Id, cancellationToken);
            if (definition is not null)
                AddJsonSource(sources, $"teamhub-flow-{summary.Id:N}", $"Flow Designer diagram: {summary.Name}", definition);
        }

        var published = await publications.ListPublishedAsync(cancellationToken);
        var matchingPublished = SelectRelevant(published, question,
            item => $"flow designer published diagram process {item.Name} {item.Description} {item.DiagramType}", 100);
        AddJsonSource(sources, "teamhub-published-flows", "Published Flow Designer diagrams", matchingPublished);
    }

    private async Task AddNotificationsAsync(
        ICollection<AiChatSource> sources,
        string userId,
        string question,
        CancellationToken cancellationToken)
    {
        if (!ContainsAny(question, "notification", "notifications", "unread", "alert", "alerts")) return;
        var inbox = await notifications.GetUnreadAsync(userId, 100, cancellationToken);
        AddJsonSource(sources, "teamhub-notifications", "Signed-in user's unread notifications", inbox);
    }

    private static IEnumerable<T> SelectRelevant<T>(
        IEnumerable<T> values,
        string question,
        Func<T, string> searchableText,
        int limit)
    {
        var tokens = Tokens(question);
        if (tokens.Length == 0) return [];
        return values.Select(value => new
            {
                Value = value,
                Score = tokens.Count(token => searchableText(value).Contains(token, StringComparison.OrdinalIgnoreCase))
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .Take(limit)
            .Select(item => item.Value);
    }

    private static bool Matches(string question, string searchable) =>
        Tokens(question).Any(token => searchable.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string[] Tokens(string value) => value.Split(
            [' ', '\t', '\r', '\n', ',', '.', ':', ';', '/', '\\', '-', '_', '?', '!'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(token => token.Length >= 3)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(16)
        .ToArray();

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string Clip(string? value, int maximumCharacters)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters] + "…";
    }

    private static void AddJsonSource(
        ICollection<AiChatSource> sources,
        string id,
        string title,
        object value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        if (json is not "[]" and not "{}") sources.Add(new AiChatSource(id, title, json));
    }
}
