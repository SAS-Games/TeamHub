using System.Text;
using System.Text.RegularExpressions;
using TeamHub.AI.Chat.Contracts;

namespace TeamHub.Web.AI;

/// <summary>
/// Loads only the TeamHub AI playbook sections needed by the current question.
/// Source selection has already been authorized by the knowledge-source adapters;
/// this class uses their stable IDs only to reduce prompt size.
/// </summary>
public sealed class TeamHubAiInstructionFileProvider(
    IWebHostEnvironment environment,
    ILogger<TeamHubAiInstructionFileProvider> logger) : IAiChatInstructionProvider
{
    private const string CoreSection = "Core";
    private const string CombiningSection = "Combining sources";
    private const string DatesSection = "Dates and current state";
    private const string ReportsSection = "Reports and tables";
    private const string VisualsSection = "Charts and diagrams";
    private const string ToolsSection = "AI directory tools";

    private static readonly IReadOnlyDictionary<string, string> SourceSections =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["teamhub-team-directory"] = "Team directory",
            ["teamhub-team-specializations"] = "Team directory",
            ["teamhub-milestones"] = "Milestone Tracker",
            ["teamhub-projects"] = "Studios and projects",
            ["teamhub-milestone-reviews"] = "Milestone reviews",
            ["teamhub-my-workflows"] = "Work Center",
            ["teamhub-my-tasks"] = "Work Center",
            ["teamhub-support-summary-period"] = "Support Summary",
            ["teamhub-support-active-milestones"] = "Support Summary",
            ["teamhub-support-expected-deliveries"] = "Support Summary",
            ["teamhub-support-build-reviews"] = "Support Summary",
            ["teamhub-support-milestone-status"] = "Support Summary",
            ["teamhub-support-jira"] = "Jira enrichment",
            ["teamhub-support-jira-status"] = "Jira enrichment",
            ["teamhub-support-jira-consent"] = "Jira enrichment",
            ["teamhub-support-effort"] = "Jira enrichment",
            ["teamhub-support-effort-status"] = "Jira enrichment",
            ["teamhub-support-effort-consent"] = "Jira enrichment",
            ["teamhub-support-confluence"] = "Confluence enrichment",
            ["teamhub-support-confluence-status"] = "Confluence enrichment",
            ["teamhub-support-confluence-consent"] = "Confluence enrichment",
            ["teamhub-home"] = "Home page and useful links",
            ["teamhub-achievements"] = "Team achievements",
            ["teamhub-custom-tabs"] = "Custom tabs and tables",
            ["teamhub-flow-designer"] = "Flow Designer",
            ["teamhub-published-flows"] = "Flow Designer",
            ["teamhub-notifications"] = "Notifications"
        };

    private static readonly Regex DateTerms = Terms(
        "current", "latest", "next", "upcoming", "today", "date", "schedule", "overdue", "week", "month");
    private static readonly Regex ReportTerms = Terms(
        "report", "table", "format", "compare", "comparison", "spreadsheet");
    private static readonly Regex VisualTerms = Terms(
        "chart", "graph", "diagram", "flowchart", "infographic", "visualization", "visualisation");
    private static readonly Regex ToolTerms = Terms(
        "member", "members", "employee", "employees", "directory", "specialization", "specializations",
        "studio", "studios", "project", "projects", "milestone review", "milestone reviews",
        "support", "jira", "ticket", "tickets", "confluence", "weekly update", "weekly updates",
        "effort", "worklog", "worklogs", "logged hours", "hours spent");

    private readonly string path = Path.Combine(environment.ContentRootPath, "AI", "TeamHubAI.md");

    public async Task<string> GetTeamHubInstructionsAsync(
        string question,
        IReadOnlyList<AiChatSource> sources,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(path))
            {
                logger.LogWarning("TeamHub AI playbook was not found at {Path}; core chat rules will be used.", path);
                return string.Empty;
            }

            var content = await File.ReadAllTextAsync(path, cancellationToken);
            return SelectSections(content, question, sources);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception,
                "TeamHub AI playbook at {Path} could not be read; core chat rules will be used.", path);
            return string.Empty;
        }
    }

    internal static string SelectSections(
        string content,
        string question,
        IReadOnlyList<AiChatSource> sources)
    {
        var sections = ParseSections(content);
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceSectionCount = 0;

        foreach (var source in sources)
        {
            var section = ResolveSourceSection(source.Id);
            if (section is not null && selected.Add(section)) sourceSectionCount++;
        }

        if (sources.Count > 0) selected.Add(CoreSection);
        if (sourceSectionCount > 1) selected.Add(CombiningSection);
        if (DateTerms.IsMatch(question)) selected.Add(DatesSection);
        if (ReportTerms.IsMatch(question)) selected.Add(ReportsSection);
        if (VisualTerms.IsMatch(question)) selected.Add(VisualsSection);
        if (ToolTerms.IsMatch(question)) selected.Add(ToolsSection);

        // The reusable chat module already supplies the security and response-format
        // rules. A source-free, ordinary question needs no TeamHub playbook text.
        if (selected.Count == 0) return string.Empty;
        selected.Add(CoreSection);

        var result = new StringBuilder();
        foreach (var section in sections)
        {
            if (!selected.Contains(section.Title)) continue;
            if (result.Length > 0) result.AppendLine().AppendLine();
            result.Append("## ").AppendLine(section.Title);
            result.Append(section.Content.Trim());
        }
        return result.ToString().Trim();
    }

    private static string? ResolveSourceSection(string sourceId)
    {
        if (SourceSections.TryGetValue(sourceId, out var section)) return section;
        if (sourceId.StartsWith("teamhub-tool-", StringComparison.OrdinalIgnoreCase)
            && sourceId.Contains("support_effort", StringComparison.OrdinalIgnoreCase))
            return "Jira enrichment";
        if (sourceId.StartsWith("teamhub-tool-list_team_", StringComparison.OrdinalIgnoreCase)
            || sourceId.StartsWith("teamhub-tool-get_team_", StringComparison.OrdinalIgnoreCase))
            return "Team directory";
        if (sourceId.StartsWith("teamhub-tool-list_studio", StringComparison.OrdinalIgnoreCase)
            || sourceId.StartsWith("teamhub-tool-get_studio", StringComparison.OrdinalIgnoreCase))
        {
            if (sourceId.Contains("milestone_review", StringComparison.OrdinalIgnoreCase))
                return "Milestone reviews";
            if (sourceId.Contains("support_overview", StringComparison.OrdinalIgnoreCase))
                return "Support Summary";
            if (sourceId.Contains("jira", StringComparison.OrdinalIgnoreCase))
                return "Jira enrichment";
            if (sourceId.Contains("weekly_updates", StringComparison.OrdinalIgnoreCase))
                return "Confluence enrichment";
            return "Studios and projects";
        }
        return sourceId.StartsWith("teamhub-flow-", StringComparison.OrdinalIgnoreCase)
            ? "Flow Designer"
            : null;
    }

    private static Regex Terms(params string[] terms) => new(
        $@"\b(?:{string.Join('|', terms.Select(Regex.Escape))})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static IReadOnlyList<PlaybookSection> ParseSections(string content)
    {
        var result = new List<PlaybookSection>();
        string? title = null;
        var body = new StringBuilder();

        foreach (var line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                AddCurrent();
                title = line[3..].Trim();
                continue;
            }
            if (title is not null) body.AppendLine(line);
        }

        AddCurrent();
        return result;

        void AddCurrent()
        {
            if (title is not null) result.Add(new PlaybookSection(title, body.ToString()));
            body.Clear();
        }
    }

    private sealed record PlaybookSection(string Title, string Content);
}
