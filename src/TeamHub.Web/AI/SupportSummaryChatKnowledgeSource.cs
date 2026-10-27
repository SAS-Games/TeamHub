using System.Text.Json;
using TeamHub.AI.Chat.Contracts;
using TeamHub.Authentication;
using TeamHub.Milestones;
using TeamHub.Studio;
using TeamHub.Web.AccessControl;
using TeamHub.Web.ExternalProcesses.WorklogAnalytics;

namespace TeamHub.Web.AI;

/// <summary>
/// Supplies the same current-period Jira, Confluence, planning, and effort data
/// shown by Support Summary. Credentials remain inside the underlying services.
/// </summary>
public sealed class SupportSummaryChatKnowledgeSource(
    ICurrentAccessService access,
    IStudioDirectoryService studios,
    IMilestoneTrackerService milestones,
    ISupportSummaryPlanningService planning,
    IStudioJiraTicketService jira,
    IStudioConfluenceUpdateService confluence,
    IWorklogEffortService effort) : IAiChatKnowledgeSource
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
        if (!await access.CanAsync(TeamHubModules.SupportSummary, AccessLevel.ReadOnly, cancellationToken))
            return [];

        var activeStudios = (await studios.GetStudiosAsync(cancellationToken))
            .Where(item => item.IsActive)
            .ToList();
        if (!IsSupportSummaryQuestion(question, activeStudios)) return [];

        var today = DateOnly.FromDateTime(DateTime.Today);
        var startDate = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var endDate = startDate.AddDays(4);
        // Chat enrichment is intentionally limited to the signed-in user's own
        // Atlassian connection. Consent never authorizes a shared/default token.
        const bool allowSharedCredential = false;
        var sources = new List<AiChatSource>();

        AddJsonSource(sources, "teamhub-support-summary-period", "Support Summary current reporting period", new
        {
            StartDate = startDate,
            EndDate = endDate,
            ActiveStudios = activeStudios.Select(item => new
            {
                item.Id,
                item.StudioName,
                item.ProjectName,
                item.StudioGroup
            })
        });

        await AddActiveMilestonesAsync(sources, activeStudios, today, cancellationToken);

        var expectedDeliveries = await planning.GetExpectedDeliveriesAsync(cancellationToken);
        AddJsonSource(sources, "teamhub-support-expected-deliveries", "Support Summary expected milestone deliveries",
            expectedDeliveries);
        var buildReviews = await planning.GetBuildReviewsAsync(cancellationToken);
        AddJsonSource(sources, "teamhub-support-build-reviews", "Support Summary milestone build reviews", buildReviews);

        if (user.HasApprovedExternalSource(AiChatExternalSources.Jira))
        {
            await AddJiraAsync(
                sources, activeStudios, user.UserId, allowSharedCredential, startDate, endDate, cancellationToken);
        }
        else
        {
            AddJsonSource(sources, "teamhub-support-jira-consent", "Support Summary Jira access", new
            {
                Message = "Jira was not accessed because the user did not approve Jira access for this question."
            });
        }

        if (user.HasApprovedExternalSource(AiChatExternalSources.Confluence))
        {
            await AddConfluenceAsync(
                sources, activeStudios, user.UserId, allowSharedCredential, startDate, endDate, cancellationToken);
        }
        else
        {
            AddJsonSource(sources, "teamhub-support-confluence-consent", "Support Summary Confluence access", new
            {
                Message = "Confluence was not accessed because the user did not approve Confluence access for this question."
            });
        }

        if (ContainsAny(question, "effort", "worklog", "work log", "logged hours", "hours spent"))
        {
            if (user.HasApprovedExternalSource(AiChatExternalSources.Jira))
            {
                await AddEffortAsync(
                    sources, user.UserId, allowSharedCredential, startDate, endDate, cancellationToken);
            }
            else
            {
                AddJsonSource(sources, "teamhub-support-effort-consent", "Support Summary effort access", new
                {
                    Message = "Jira worklogs were not accessed because the user did not approve Jira access for this question."
                });
            }
        }

        return sources;
    }

    private async Task AddActiveMilestonesAsync(
        ICollection<AiChatSource> sources,
        IReadOnlyList<StudioDetails> activeStudios,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        try
        {
            var all = await milestones.GetMilestonesAsync(cancellationToken);
            var active = activeStudios.Select(studio =>
            {
                var next = all
                    .Where(item => !item.Milestone.StartsWith("Total MS", StringComparison.OrdinalIgnoreCase))
                    .Where(item => string.Equals(item.Title, studio.ProjectName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(item.Program, studio.ProjectName, StringComparison.OrdinalIgnoreCase))
                    .Where(item => !item.DeliveryDate.HasValue
                        || DateOnly.FromDateTime(item.DeliveryDate.Value) >= today)
                    .OrderBy(item => item.DeliveryDate ?? DateTime.MaxValue)
                    .FirstOrDefault();
                return next is null ? null : new
                {
                    studio.StudioName,
                    studio.ProjectName,
                    next.Developer,
                    next.Milestone,
                    next.Description,
                    next.DeliveryDate,
                    next.MsApprovalDate,
                    next.ReleaseDate
                };
            }).Where(item => item is not null);
            AddJsonSource(sources, "teamhub-support-active-milestones", "Support Summary active milestones", active);
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidDataException
            or InvalidOperationException)
        {
            AddJsonSource(sources, "teamhub-support-milestone-status", "Support Summary milestone availability",
                new { Message = exception.Message });
        }
    }

    private async Task AddJiraAsync(
        ICollection<AiChatSource> sources,
        IReadOnlyList<StudioDetails> activeStudios,
        string userId,
        bool allowSharedCredential,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await jira.GetTicketsForStudiosAsync(new StudioJiraTicketBatchQuery
            {
                StudioIds = activeStudios.Select(item => item.Id).ToList(),
                RequestingUserId = userId,
                AllowPrivilegedDefaultCredential = allowSharedCredential,
                ActiveSprintOnly = false,
                StartDate = startDate,
                EndDate = endDate,
                UseSprintDateRange = true
            }, cancellationToken);
            var studiosById = activeStudios.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            var tickets = results
                .SelectMany(studioResult => studioResult.Value.Groups.SelectMany(group =>
                    group.Tickets.Select(ticket => new
                    {
                        Studio = studiosById.TryGetValue(studioResult.Key, out var studio)
                            ? studio.StudioName
                            : studioResult.Key,
                        Project = studiosById.TryGetValue(studioResult.Key, out studio)
                            ? studio.ProjectName
                            : string.Empty,
                        SupportType = group.Name,
                        ticket.TicketId,
                        Summary = Clip(ticket.Summary, 1_000),
                        ticket.AssignedUser,
                        ticket.Status,
                        ticket.Priority,
                        ticket.Url,
                        ticket.Components
                    })))
                .Take(50)
                .ToList();
            AddJsonSource(sources, "teamhub-support-jira", "Support Summary Jira tickets", tickets);

            var messages = results
                .Where(item => !string.IsNullOrWhiteSpace(item.Value.Message))
                .Select(item => new { StudioId = item.Key, item.Value.Message })
                .ToList();
            AddJsonSource(sources, "teamhub-support-jira-status", "Support Summary Jira availability", messages);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException
            or InvalidDataException or ArgumentException)
        {
            AddJsonSource(sources, "teamhub-support-jira-status", "Support Summary Jira availability",
                new { Message = exception.Message });
        }
    }

    private async Task AddConfluenceAsync(
        ICollection<AiChatSource> sources,
        IReadOnlyList<StudioDetails> activeStudios,
        string userId,
        bool allowSharedCredential,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await confluence.GetConsolidatedUpdatesAsync(
                new ConsolidatedStudioConfluenceUpdateQuery
                {
                    StudioIds = activeStudios.Select(item => item.Id).ToList(),
                    RequestingUserId = userId,
                    AllowPrivilegedDefaultCredential = allowSharedCredential,
                    StartDate = startDate,
                    EndDate = endDate
                }, cancellationToken);
            var studiosById = activeStudios.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            var updates = result.Updates.Take(10).Select(update => new
            {
                Studio = studiosById.TryGetValue(update.StudioId, out var studio)
                    ? studio.StudioName
                    : update.StudioId,
                Project = studiosById.TryGetValue(update.StudioId, out studio)
                    ? studio.ProjectName
                    : string.Empty,
                update.WeekStart,
                update.WeekEnd,
                update.PageTitle,
                update.PageUrl,
                Overview = Clip(update.Overview),
                StudioWork = Clip(update.StudioWork),
                HpgdsSupport = Clip(update.HpgdsSupport),
                WmdSupport = Clip(update.WmdSupport),
                ActionItems = Clip(update.ActionItems),
                Notes = Clip(update.Notes),
                Message = Clip(update.Message)
            });
            AddJsonSource(sources, "teamhub-support-confluence", "Support Summary Confluence weekly updates", updates);
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                AddJsonSource(sources, "teamhub-support-confluence-status", "Support Summary Confluence availability",
                    new { result.Message, result.UnconfiguredStudioIds });
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException
            or InvalidDataException or FormatException)
        {
            AddJsonSource(sources, "teamhub-support-confluence-status", "Support Summary Confluence availability",
                new { Message = exception.Message });
        }
    }

    private async Task AddEffortAsync(
        ICollection<AiChatSource> sources,
        string userId,
        bool allowSharedCredential,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        try
        {
            var report = await effort.GetActualEffortAsync(
                startDate, endDate, userId, allowSharedCredential, cancellationToken);
            AddJsonSource(sources, "teamhub-support-effort", "Support Summary logged effort", report);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException
            or ArgumentException)
        {
            AddJsonSource(sources, "teamhub-support-effort-status", "Support Summary effort availability",
                new { Message = exception.Message });
        }
    }

    private static bool IsSupportSummaryQuestion(string question, IEnumerable<StudioDetails> activeStudios) =>
        ContainsAny(question,
            "support summary", "support", "jira", "ticket", "issue", "confluence", "weekly update",
            "action item", "expected delivery", "build review", "effort", "worklog", "logged hours")
        || activeStudios.Any(studio =>
            ContainsPhrase(question, studio.StudioName) || ContainsPhrase(question, studio.ProjectName));

    /// <summary>
    /// Determines which external systems could enrich a Support Summary question.
    /// This method does not access either system; the page uses it to request
    /// explicit, per-question consent before invoking the knowledge source.
    /// </summary>
    public static IReadOnlyList<string> GetRequestedExternalSources(string question)
    {
        var requested = new List<string>();
        var isGeneralSupportQuestion = ContainsAny(question,
            "support summary", "support activity", "support activities", "current support", "ongoing support");
        if (isGeneralSupportQuestion || ContainsAny(question,
                "jira", "ticket", "tickets", "issue", "issues", "effort", "worklog", "work log",
                "logged hours", "hours spent"))
        {
            requested.Add(AiChatExternalSources.Jira);
        }

        if (isGeneralSupportQuestion || ContainsAny(question,
                "confluence", "weekly update", "weekly updates", "action item", "action items",
                "studio work", "hpgds support", "wmd support"))
        {
            requested.Add(AiChatExternalSources.Confluence);
        }

        return requested;
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => ContainsPhrase(value, term));

    private static bool ContainsPhrase(string value, string? term) =>
        !string.IsNullOrWhiteSpace(term)
        && value.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Clip(string? value, int maximumCharacters = 1_200)
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
