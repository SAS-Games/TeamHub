using System.Globalization;
using System.Text.Json;
using TeamHub.AI.Chat.Contracts;
using TeamHub.Authentication;
using TeamHub.Milestones;
using TeamHub.MilestoneReview;
using TeamHub.Studio;
using TeamHub.Web.AccessControl;
using TeamHub.Web.ExternalProcesses.WorklogAnalytics;
using Review = TeamHub.MilestoneReview.MilestoneReview;

namespace TeamHub.Web.AI;

/// <summary>
/// Read-only milestone-review and studio-support tools available to TeamHub AI Chat.
/// External systems are queried only after explicit approval for the current question.
/// </summary>
public sealed class TeamHubAiStudioSupportTools(
    ICurrentAccessService access,
    IStudioDirectoryService studios,
    IMilestoneTrackerService milestones,
    IMilestoneReviewService milestoneReviews,
    ISupportSummaryPlanningService planning,
    IStudioJiraTicketService jira,
    IStudioConfluenceUpdateService confluence,
    IWorklogEffortService effort,
    ILogger<TeamHubAiStudioSupportTools> logger) : IAiChatToolProvider
{
    private const int MaximumReviews = 50;
    private const int MaximumTickets = 50;
    private const int MaximumUpdates = 12;
    private const int MaximumRangeDays = 370;
    private const int MaximumEffortRangeDays = 366;
    private const int MaximumEffortRows = 60;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<AiChatToolDefinition> Definitions { get; } =
    [
        new("list_studio_milestone_reviews",
            "List active milestone reviews for one studio or project. Historical reviews are included only when requested.",
            "{\"studio\":\"required studio ID, studio name, or project name\",\"includeHistory\":false}"),
        new("get_studio_milestone_review",
            "Get milestone-review details and attributed feedback by review ID, project name, milestone name, or Jira ticket ID.",
            "{\"query\":\"required review ID, project, milestone, or Jira ticket ID\"}"),
        new("get_studio_support_overview",
            "Get TeamHub-owned support-planning information for one studio: its current or upcoming milestones, expected deliveries, and build reviews.",
            "{\"studio\":\"required studio ID, studio name, or project name\"}"),
        new("get_studio_jira_tickets",
            "Get Jira support tickets for one studio. Requires the user's Jira approval for this question and uses that user's personal Jira credential.",
            "{\"studio\":\"required studio ID, studio name, or project name\",\"startDate\":\"optional yyyy-MM-dd\",\"endDate\":\"optional yyyy-MM-dd\"}",
            AiChatExternalSources.Jira),
        new("get_studio_weekly_updates",
            "Get Confluence weekly updates for one studio. Requires the user's Confluence approval for this question and uses that user's personal Confluence credential.",
            "{\"studio\":\"required studio ID, studio name, or project name\",\"startDate\":\"optional yyyy-MM-dd\",\"endDate\":\"optional yyyy-MM-dd\"}",
            AiChatExternalSources.Confluence),
        new("get_support_effort_summary",
            "Get aggregated Jira worklog effort by support category and studio for a date range. Use for overall effort reports and cross-studio comparisons; raw worklog comments are never returned.",
            "{\"startDate\":\"optional yyyy-MM-dd; defaults to 30 days ago\",\"endDate\":\"optional yyyy-MM-dd; defaults to today\"}",
            AiChatExternalSources.Jira),
        new("get_studio_support_effort",
            "Get aggregated Jira worklog hours for one studio or project over a date range. Raw worklog comments are never returned.",
            "{\"studio\":\"required studio ID, studio name, or project name\",\"startDate\":\"optional yyyy-MM-dd\",\"endDate\":\"optional yyyy-MM-dd\"}",
            AiChatExternalSources.Jira),
        new("get_employee_support_effort",
            "Get aggregated Jira worklog effort categories for employees matching a name over a date range. Raw worklog comments are never returned.",
            "{\"employee\":\"required employee name\",\"startDate\":\"optional yyyy-MM-dd\",\"endDate\":\"optional yyyy-MM-dd\"}",
            AiChatExternalSources.Jira)
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
        var isReviewTool = toolName is "list_studio_milestone_reviews" or "get_studio_milestone_review";
        var isSupportTool = toolName is "get_studio_support_overview" or "get_studio_jira_tickets"
            or "get_studio_weekly_updates" or "get_support_effort_summary"
            or "get_studio_support_effort" or "get_employee_support_effort";
        if (!isReviewTool && !isSupportTool)
            throw new AiChatException($"The TeamHub tool '{toolName}' is not available.");
        if (isReviewTool
            && !await access.CanAsync(TeamHubModules.MilestoneReview, AccessLevel.ReadOnly, cancellationToken))
            throw new AiChatException("You do not have access to TeamHub milestone reviews.");
        if (isSupportTool
            && !await access.CanAsync(TeamHubModules.SupportSummary, AccessLevel.ReadOnly, cancellationToken))
            throw new AiChatException("You do not have access to TeamHub studio support information.");

        logger.LogInformation("TeamHub AI tool {ToolName} executed for {UserId}", toolName, user.UserId);
        return toolName switch
        {
            "list_studio_milestone_reviews" => await ListMilestoneReviewsAsync(arguments, cancellationToken),
            "get_studio_milestone_review" => await GetMilestoneReviewAsync(arguments, cancellationToken),
            "get_studio_support_overview" => await GetSupportOverviewAsync(arguments, cancellationToken),
            "get_studio_jira_tickets" => await GetJiraTicketsAsync(user, arguments, cancellationToken),
            "get_studio_weekly_updates" => await GetWeeklyUpdatesAsync(user, arguments, cancellationToken),
            "get_support_effort_summary" => await GetSupportEffortSummaryAsync(user, arguments, cancellationToken),
            "get_studio_support_effort" => await GetStudioSupportEffortAsync(user, arguments, cancellationToken),
            "get_employee_support_effort" => await GetEmployeeSupportEffortAsync(user, arguments, cancellationToken),
            _ => throw new AiChatException($"The TeamHub tool '{toolName}' is not available.")
        };
    }

    private async Task<AiChatSource> ListMilestoneReviewsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var studio = await ResolveStudioAsync(RequiredArgument(arguments, "studio"), cancellationToken);
        var includeHistory = OptionalBooleanArgument(arguments, "includeHistory");
        var reviews = (await milestoneReviews.GetActiveAsync(cancellationToken)).AsEnumerable();
        if (includeHistory)
            reviews = reviews.Concat(await milestoneReviews.GetHistoryAsync(cancellationToken));

        var matching = reviews
            .Where(review => MatchesStudio(review.ProjectName, studio))
            .DistinctBy(review => review.Id)
            .OrderByDescending(review => review.PublishedAt ?? review.CreatedAt)
            .ToList();
        return Source("list_studio_milestone_reviews", $"Milestone reviews: {studio.StudioName}", new
        {
            studio = StudioIdentity(studio),
            includeHistory,
            totalMatches = matching.Count,
            truncated = matching.Count > MaximumReviews,
            reviews = matching.Take(MaximumReviews).Select(ReviewSummary)
        });
    }

    private async Task<AiChatSource> GetMilestoneReviewAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var query = RequiredArgument(arguments, "query");
        var all = (await milestoneReviews.GetActiveAsync(cancellationToken))
            .Concat(await milestoneReviews.GetHistoryAsync(cancellationToken))
            .DistinctBy(review => review.Id)
            .ToList();
        var exact = all.Where(review => IsExactReviewMatch(review, query)).ToList();
        var matching = (exact.Count > 0
                ? exact
                : all.Where(review => ReviewSearchText(review).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList())
            .OrderByDescending(review => review.PublishedAt ?? review.CreatedAt)
            .Take(10)
            .Select(review => new
            {
                review.Id,
                review.ProjectName,
                review.MilestoneName,
                review.Description,
                status = review.Status.ToString(),
                review.CreatedAt,
                review.PublishedAt,
                review.JiraTicketId,
                pic = review.Pic is null ? null : new { review.Pic.DisplayName, review.Pic.UserId },
                assignedReviewers = review.AssignedReviewers.Select(item => new { item.DisplayName, item.UserId }),
                reportGenerators = review.ReportGenerators.Select(item => new { item.DisplayName, item.UserId }),
                feedback = review.Feedback.Select(item => new
                {
                    item.Reviewer,
                    sessionDetails = Clip(item.SessionDetails),
                    whatGood = Clip(item.WhatGood),
                    needImprovements = Clip(item.NeedImprovements),
                    additionalFeedback = Clip(item.AdditionalFeedback)
                })
            })
            .ToList();
        return Source("get_studio_milestone_review", "TeamHub milestone review details", new
        {
            query,
            matchCount = matching.Count,
            reviews = matching
        });
    }

    private async Task<AiChatSource> GetSupportOverviewAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var studio = await ResolveStudioAsync(RequiredArgument(arguments, "studio"), cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var matchingMilestones = (await milestones.GetMilestonesAsync(cancellationToken))
            .Where(item => !item.Milestone.StartsWith("Total MS", StringComparison.OrdinalIgnoreCase))
            .Where(item => MatchesStudio(item.Title, studio) || MatchesStudio(item.Program, studio))
            .Where(item => !item.DeliveryDate.HasValue || DateOnly.FromDateTime(item.DeliveryDate.Value) >= today)
            .OrderBy(item => item.DeliveryDate ?? DateTime.MaxValue)
            .Take(20)
            .Select(item => new
            {
                item.Program,
                item.Title,
                item.Developer,
                item.Milestone,
                item.Description,
                item.DeliveryDate,
                item.MsApprovalDate,
                item.ReleaseDate
            });
        var deliveries = (await planning.GetExpectedDeliveriesAsync(cancellationToken))
            .Where(item => MatchesStudio(item.ProjectName, studio))
            .OrderBy(item => item.ExpectedDeliveryDate)
            .Take(30);
        var buildReviews = (await planning.GetBuildReviewsAsync(cancellationToken))
            .Where(item => MatchesStudio(item.ProjectName, studio))
            .OrderBy(item => item.Eta)
            .Take(30);

        return Source("get_studio_support_overview", $"Studio support overview: {studio.StudioName}", new
        {
            studio = StudioIdentity(studio),
            asOf = today,
            milestones = matchingMilestones,
            expectedDeliveries = deliveries,
            buildReviews
        });
    }

    private async Task<AiChatSource> GetJiraTicketsAsync(
        AiChatUserContext user,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!user.HasApprovedExternalSource(AiChatExternalSources.Jira))
            throw new AiChatException("Jira approval is required for this question before studio tickets can be accessed.");
        var studio = await ResolveStudioAsync(RequiredArgument(arguments, "studio"), cancellationToken);
        var (startDate, endDate) = ReadDateRange(arguments);
        var result = await jira.GetTicketsAsync(new StudioJiraTicketQuery
        {
            StudioId = studio.Id,
            RequestingUserId = user.UserId,
            AllowPrivilegedDefaultCredential = false,
            ActiveSprintOnly = !startDate.HasValue && !endDate.HasValue,
            StartDate = startDate,
            EndDate = endDate,
            UseSprintDateRange = false
        }, cancellationToken);
        var tickets = result.Groups.SelectMany(group => group.Tickets.Select(ticket => new
            {
                supportType = group.Name,
                ticket.TicketId,
                summary = Clip(ticket.Summary, 1_000),
                ticket.AssignedUser,
                ticket.Status,
                ticket.Priority,
                ticket.Url,
                ticket.Components
            }))
            .Take(MaximumTickets)
            .ToList();
        return Source("get_studio_jira_tickets", $"Studio Jira tickets: {studio.StudioName}", new
        {
            studio = StudioIdentity(studio),
            startDate,
            endDate,
            activeSprintOnly = !startDate.HasValue && !endDate.HasValue,
            result.IsConfigured,
            result.IsReadOnly,
            result.Message,
            totalReturned = tickets.Count,
            tickets
        });
    }

    private async Task<AiChatSource> GetWeeklyUpdatesAsync(
        AiChatUserContext user,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!user.HasApprovedExternalSource(AiChatExternalSources.Confluence))
            throw new AiChatException("Confluence approval is required for this question before weekly updates can be accessed.");
        var studio = await ResolveStudioAsync(RequiredArgument(arguments, "studio"), cancellationToken);
        var (startDate, endDate) = ReadDateRange(arguments);
        var result = await confluence.GetUpdatesAsync(new StudioConfluenceUpdateQuery
        {
            StudioId = studio.Id,
            RequestingUserId = user.UserId,
            AllowPrivilegedDefaultCredential = false,
            StartDate = startDate,
            EndDate = endDate
        }, cancellationToken);
        var updates = result.Updates.Take(MaximumUpdates).Select(update => new
        {
            update.WeekStart,
            update.WeekEnd,
            update.PageTitle,
            update.PageUrl,
            update.PageFound,
            update.StudioFound,
            overview = Clip(update.Overview),
            studioWork = Clip(update.StudioWork),
            hpgdsSupport = Clip(update.HpgdsSupport),
            wmdSupport = Clip(update.WmdSupport),
            actionItems = Clip(update.ActionItems),
            notes = Clip(update.Notes),
            message = Clip(update.Message)
        });
        return Source("get_studio_weekly_updates", $"Studio weekly updates: {studio.StudioName}", new
        {
            studio = StudioIdentity(studio),
            startDate,
            endDate,
            result.IsConfigured,
            result.IsReadOnly,
            result.Message,
            updates
        });
    }

    private async Task<AiChatSource> GetSupportEffortSummaryAsync(
        AiChatUserContext user,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var (startDate, endDate) = ReadEffortDateRange(arguments);
        var report = await LoadEffortAsync(user, startDate, endDate, cancellationToken);
        var summary = report.EffortSummary.Take(MaximumEffortRows).ToList();
        var studios = report.StudioBreakdown.Take(MaximumEffortRows).ToList();
        return Source("get_support_effort_summary", "Support effort summary", new
        {
            startDate,
            endDate,
            totalHours = Math.Round(summary.Sum(item => item.Hours), 2),
            effortCategories = summary,
            studioBreakdown = studios,
            truncated = report.EffortSummary.Count > MaximumEffortRows
                || report.StudioBreakdown.Count > MaximumEffortRows
        });
    }

    private async Task<AiChatSource> GetStudioSupportEffortAsync(
        AiChatUserContext user,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var studio = await ResolveStudioAsync(RequiredArgument(arguments, "studio"), cancellationToken);
        var (startDate, endDate) = ReadEffortDateRange(arguments);
        var report = await LoadEffortAsync(user, startDate, endDate, cancellationToken);
        var matching = report.StudioBreakdown
            .Where(item => IsStudioEffortLabel(item.Label, studio))
            .OrderByDescending(item => item.Hours)
            .Take(MaximumEffortRows)
            .ToList();
        return Source("get_studio_support_effort", $"Studio support effort: {studio.StudioName}", new
        {
            studio = StudioIdentity(studio),
            startDate,
            endDate,
            totalHours = Math.Round(matching.Sum(item => item.Hours), 2),
            effort = matching,
            availableStudioLabels = matching.Count == 0
                ? report.StudioBreakdown.Select(item => item.Label).Take(MaximumEffortRows)
                : null
        });
    }

    private async Task<AiChatSource> GetEmployeeSupportEffortAsync(
        AiChatUserContext user,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var employee = RequiredArgument(arguments, "employee");
        var (startDate, endDate) = ReadEffortDateRange(arguments);
        var report = await LoadEffortAsync(user, startDate, endDate, cancellationToken);
        var matching = report.EmployeeEffortBreakdowns
            .Where(item => item.Employee.Contains(employee, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => string.Equals(item.Employee, employee, StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.Employee, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select(item => new
            {
                item.Employee,
                totalHours = Math.Round(item.Slices.Sum(slice => slice.Hours), 2),
                effortCategories = item.Slices.Take(30)
            })
            .ToList();
        return Source("get_employee_support_effort", "Employee support effort", new
        {
            employee,
            startDate,
            endDate,
            matchCount = matching.Count,
            employees = matching
        });
    }

    private async Task<WorklogEffortReport> LoadEffortAsync(
        AiChatUserContext user,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        if (!user.HasApprovedExternalSource(AiChatExternalSources.Jira))
            throw new AiChatException("Jira approval is required for this question before support effort can be accessed.");
        return await effort.GetActualEffortAsync(
            startDate,
            endDate,
            user.UserId,
            false,
            cancellationToken);
    }

    private static (DateOnly StartDate, DateOnly EndDate) ReadEffortDateRange(JsonElement arguments)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var startDate = OptionalDateArgument(arguments, "startDate");
        var endDate = OptionalDateArgument(arguments, "endDate") ?? today;
        startDate ??= endDate.AddDays(-29);
        if (endDate < startDate.Value)
            throw new AiChatException("Tool argument 'endDate' must be on or after 'startDate'.");
        if (endDate.DayNumber - startDate.Value.DayNumber >= MaximumEffortRangeDays)
            throw new AiChatException($"Support effort date ranges cannot exceed {MaximumEffortRangeDays} days.");
        return (startDate.Value, endDate);
    }

    private static bool IsStudioEffortLabel(string label, StudioDetails studio) =>
        string.Equals(label, studio.Id, StringComparison.OrdinalIgnoreCase)
        || string.Equals(label, studio.StudioName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(label, studio.ProjectName, StringComparison.OrdinalIgnoreCase)
        || label.Contains(studio.StudioName, StringComparison.OrdinalIgnoreCase)
        || label.Contains(studio.ProjectName, StringComparison.OrdinalIgnoreCase);

    private async Task<StudioDetails> ResolveStudioAsync(string query, CancellationToken cancellationToken)
    {
        var active = (await studios.GetStudiosAsync(cancellationToken)).Where(item => item.IsActive).ToList();
        var exact = active.Where(item =>
                string.Equals(item.Id, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.StudioName, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.ProjectName, query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count == 1) return exact[0];
        var partial = active.Where(item =>
                item.StudioName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.ProjectName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (partial.Count == 1) return partial[0];
        if (exact.Count == 0 && partial.Count == 0)
            throw new AiChatException($"No active studio matched '{query}'.");
        var names = string.Join(", ", (exact.Count > 0 ? exact : partial).Select(item => item.StudioName));
        throw new AiChatException($"Studio '{query}' is ambiguous. Matching studios: {names}.");
    }

    private static object StudioIdentity(StudioDetails studio) => new
    {
        studio.Id,
        studio.StudioName,
        studio.ProjectName
    };

    private static object ReviewSummary(Review review) => new
    {
        review.Id,
        review.ProjectName,
        review.MilestoneName,
        review.Description,
        status = review.Status.ToString(),
        review.CreatedAt,
        review.PublishedAt,
        review.JiraTicketId,
        pic = review.Pic?.DisplayName,
        assignedReviewers = review.AssignedReviewers.Select(item => item.DisplayName),
        feedbackCount = review.Feedback.Count
    };

    private static bool MatchesStudio(string value, StudioDetails studio) =>
        string.Equals(value, studio.ProjectName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, studio.StudioName, StringComparison.OrdinalIgnoreCase);

    private static bool IsExactReviewMatch(Review review, string query) =>
        Guid.TryParse(query, out var id) && review.Id == id
        || string.Equals(review.ProjectName, query, StringComparison.OrdinalIgnoreCase)
        || string.Equals(review.MilestoneName, query, StringComparison.OrdinalIgnoreCase)
        || string.Equals(review.JiraTicketId, query, StringComparison.OrdinalIgnoreCase);

    private static string ReviewSearchText(Review review) =>
        $"{review.Id} {review.ProjectName} {review.MilestoneName} {review.Description} {review.JiraTicketId}";

    private static (DateOnly? StartDate, DateOnly? EndDate) ReadDateRange(JsonElement arguments)
    {
        var startDate = OptionalDateArgument(arguments, "startDate");
        var endDate = OptionalDateArgument(arguments, "endDate");
        if (startDate.HasValue && endDate.HasValue)
        {
            if (endDate < startDate)
                throw new AiChatException("Tool argument 'endDate' must be on or after 'startDate'.");
            if (endDate.Value.DayNumber - startDate.Value.DayNumber > MaximumRangeDays)
                throw new AiChatException($"The requested date range cannot exceed {MaximumRangeDays} days.");
        }
        return (startDate, endDate);
    }

    private static DateOnly? OptionalDateArgument(JsonElement arguments, string name)
    {
        var value = OptionalArgument(arguments, name);
        if (value is null) return null;
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : throw new AiChatException($"Tool argument '{name}' must use yyyy-MM-dd format.");
    }

    private static bool OptionalBooleanArgument(JsonElement arguments, string name)
    {
        EnsureObject(arguments);
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return false;
        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            throw new AiChatException($"Tool argument '{name}' must be true or false.");
        return value.GetBoolean();
    }

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

    private static string? Clip(string? value, int maximumLength = 2_000)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var normalized = value.Trim();
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength] + "...";
    }

    private static AiChatSource Source(string toolName, string title, object value) =>
        new($"teamhub-tool-{toolName}", title, JsonSerializer.Serialize(value, JsonOptions));
}
