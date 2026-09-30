using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Authentication;
using TeamHub.Milestones;
using TeamHub.Studio;
using TeamHub.Web.WorklogAnalytics;

namespace TeamHub.Web.Pages;

public sealed class SupportSummaryModel(
    IStudioDirectoryService studioDirectoryService,
    IMilestoneTrackerService milestoneTrackerService,
    IStudioJiraTicketService jiraTicketService,
    IStudioConfluenceUpdateService confluenceUpdateService,
    ISupportSummaryPlanningService planningService,
    IWorklogEffortService worklogEffortService) : PageModel
{
    public const string WeeklyPeriod = "Weekly";
    public const string MonthlyPeriod = "Monthly";
    public const string CustomPeriod = "Custom";

    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string ReportPeriod { get; set; } = WeeklyPeriod;
    [BindProperty(SupportsGet = true)] public string? SelectedMonth { get; set; }
    [BindProperty(SupportsGet = true)] public int? Week { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? StartDate { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? EndDate { get; set; }
    public IReadOnlyList<SupportSummaryWeekOption> WeekOptions { get; private set; } = [];
    public bool IsMonthlyReport => ReportPeriod == MonthlyPeriod;

    public IReadOnlyList<StudioDetails> Studios { get; private set; } = [];
    public IReadOnlyList<SupportSummaryMilestone> ActiveMilestones { get; private set; } = [];
    public IReadOnlyList<SupportSummaryMilestoneDescriptionOption> MilestoneDescriptionOptions { get; private set; } = [];
    public IReadOnlyList<ExpectedMilestoneDelivery> ExpectedDeliveries { get; private set; } = [];
    public IReadOnlyList<MilestoneBuildReview> BuildReviews { get; private set; } = [];
    public IReadOnlyList<string> BuildReviewStatuses => MilestoneBuildReviewStatuses.All;
    public IReadOnlyList<WorklogEffortSlice> EffortSummary { get; private set; } = [];
    public IReadOnlyList<WorklogEffortSlice> StudioEffortBreakdown { get; private set; } = [];
    public IReadOnlyList<WorklogEmployeeEffort> EmployeeEffortBreakdowns { get; private set; } = [];
    public IReadOnlyList<SupportSummaryTicket> JiraTickets { get; private set; } = [];
    public IReadOnlyList<SupportSummaryMessage> JiraMessages { get; private set; } = [];
    public StudioConfluenceUpdateResult ConfluenceResult { get; private set; } = new();
    public string? MilestoneErrorMessage { get; private set; }
    public string? ConfluenceErrorMessage { get; private set; }
    public string? EffortErrorMessage { get; private set; }
    public string? EffortErrorDisplayMessage => string.IsNullOrWhiteSpace(EffortErrorMessage)
        ? null
        : CanManagePlanning
            ? EffortErrorMessage
            : "Something went wrong while loading logged effort. Please contact an administrator.";
    public bool IsUsingSharedJiraCredential { get; private set; }
    public bool CanManagePlanning => User.IsInRole(TeamHubUserTypes.Admin);

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        SetReportRange();
        Studios = (await studioDirectoryService.GetStudiosAsync(cancellationToken))
            .Where(studio => studio.IsActive)
            .OrderBy(studio => studio.GroupDisplayOrder ?? int.MaxValue)
            .ThenBy(studio => studio.StudioGroup, StringComparer.OrdinalIgnoreCase)
            .ThenBy(studio => studio.StudioDisplayOrder ?? int.MaxValue)
            .ThenBy(studio => studio.StudioName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(studio => studio.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ExpectedDeliveries = await planningService.GetExpectedDeliveriesAsync(cancellationToken);
        BuildReviews = await planningService.GetBuildReviewsAsync(cancellationToken);
        await LoadEffortAsync(cancellationToken);
        if (Studios.Count == 0) return;

        await LoadMilestonesAsync(cancellationToken);
        await LoadJiraAsync(cancellationToken);
        await LoadConfluenceAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSaveExpectedDeliveryAsync(
        Guid? id,
        string? projectName,
        string? milestoneDescription,
        DateOnly? expectedDeliveryDate,
        CancellationToken cancellationToken)
    {
        if (!CanManagePlanning) return Forbid();
        if (!expectedDeliveryDate.HasValue)
            return PlanningError("Enter an expected delivery date.", "expected-deliveries");
        var project = await GetConfiguredProjectAsync(projectName, cancellationToken);
        if (project is null)
            return PlanningError("Select a configured active project.", "expected-deliveries");
        if (string.IsNullOrWhiteSpace(milestoneDescription))
            return PlanningError("Select a milestone description.", "expected-deliveries");

        await planningService.SaveExpectedDeliveryAsync(
            new ExpectedMilestoneDelivery(
                id ?? Guid.Empty,
                project,
                milestoneDescription,
                expectedDeliveryDate.Value),
            cancellationToken);
        StatusMessage = id.HasValue
            ? "Expected milestone build delivery updated."
            : "Expected milestone build delivery added.";
        return RedirectToPlanningTable("expected-deliveries");
    }

    public async Task<IActionResult> OnPostDeleteExpectedDeliveryAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!CanManagePlanning) return Forbid();
        await planningService.DeleteExpectedDeliveryAsync(id, cancellationToken);
        StatusMessage = "Expected milestone build delivery removed.";
        return RedirectToPlanningTable("expected-deliveries");
    }

    public async Task<IActionResult> OnPostSaveBuildReviewAsync(
        Guid? id,
        string? projectName,
        string? milestoneDescription,
        string? status,
        DateOnly? buildReceiveDate,
        DateOnly? eta,
        CancellationToken cancellationToken)
    {
        if (!CanManagePlanning) return Forbid();
        if (!eta.HasValue)
            return PlanningError("Enter an ETA.", "build-reviews");
        if (!buildReceiveDate.HasValue)
            return PlanningError("Enter the build receive date.", "build-reviews");
        var project = await GetConfiguredProjectAsync(projectName, cancellationToken);
        if (project is null)
            return PlanningError("Select a configured active project.", "build-reviews");
        if (string.IsNullOrWhiteSpace(milestoneDescription))
            return PlanningError("Select a milestone description.", "build-reviews");
        if (!MilestoneBuildReviewStatuses.All.Contains(status, StringComparer.OrdinalIgnoreCase))
            return PlanningError("Select a valid review status.", "build-reviews");

        await planningService.SaveBuildReviewAsync(
            new MilestoneBuildReview(
                id ?? Guid.Empty,
                project,
                milestoneDescription,
                status!,
                buildReceiveDate.Value,
                eta.Value),
            cancellationToken);
        StatusMessage = id.HasValue ? "Milestone build review updated." : "Milestone build review added.";
        return RedirectToPlanningTable("build-reviews");
    }

    public async Task<IActionResult> OnPostDeleteBuildReviewAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!CanManagePlanning) return Forbid();
        await planningService.DeleteBuildReviewAsync(id, cancellationToken);
        StatusMessage = "Milestone build review removed.";
        return RedirectToPlanningTable("build-reviews");
    }

    public string GetStudioName(string studioId) =>
        FindStudio(studioId)?.StudioName ?? studioId;

    public string GetStudioGroup(string studioId) =>
        FindStudio(studioId)?.StudioGroup ?? "Ungrouped";

    public double GetEffortTotal(IReadOnlyList<WorklogEffortSlice> slices) =>
        slices.Sum(slice => slice.Hours);

    public double GetEffortShare(WorklogEffortSlice slice, IReadOnlyList<WorklogEffortSlice> slices)
    {
        var total = GetEffortTotal(slices);
        return total <= 0 ? 0 : slice.Hours / total * 100;
    }

    public string GetEffortColor(int index) =>
        EffortColors[index % EffortColors.Length];

    public IReadOnlyList<string> EffortChartColors => EffortColors;

    public int GetBuildAgeingDays(DateOnly buildReceiveDate) =>
        Math.Max(0, DateOnly.FromDateTime(DateTime.Today).DayNumber - buildReceiveDate.DayNumber);

    public string BuildPieGradient(IReadOnlyList<WorklogEffortSlice> slices)
    {
        var total = GetEffortTotal(slices);
        if (total <= 0) return "#d9e1dc";

        var start = 0d;
        var stops = new List<string>();
        for (var index = 0; index < slices.Count; index++)
        {
            var end = start + slices[index].Hours / total * 100;
            stops.Add(FormattableString.Invariant(
                $"{GetEffortColor(index)} {start:0.####}% {end:0.####}%"));
            start = end;
        }
        return $"conic-gradient({string.Join(", ", stops)})";
    }

    private async Task LoadMilestonesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var today = DateTime.Today;
            var milestones = await milestoneTrackerService.GetMilestonesAsync(cancellationToken);
            var selectableMilestones = milestones
                .Where(milestone => !milestone.Milestone.StartsWith("Total MS", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var upcomingMilestones = selectableMilestones
                .Where(milestone => !milestone.DeliveryDate.HasValue || milestone.DeliveryDate.Value.Date >= today)
                .ToList();
            MilestoneDescriptionOptions = Studios
                .SelectMany(studio => selectableMilestones
                    .Where(milestone => IsProjectMilestone(studio.ProjectName, milestone))
                    .Where(milestone => !string.IsNullOrWhiteSpace(milestone.Description))
                    .OrderBy(milestone => milestone.DeliveryDate ?? DateTime.MaxValue)
                    .Select(milestone => new SupportSummaryMilestoneDescriptionOption(
                        studio.ProjectName,
                        milestone.Description.Trim())))
                .GroupBy(
                    option => $"{option.ProjectName}\0{option.Description}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            ActiveMilestones = Studios
                .Select(studio =>
                {
                    var nextMilestone = upcomingMilestones
                        .Where(milestone => IsProjectMilestone(studio.ProjectName, milestone))
                        .OrderBy(milestone => milestone.DeliveryDate ?? DateTime.MaxValue)
                        .FirstOrDefault();
                    return nextMilestone is null
                        ? null
                        : new SupportSummaryMilestone(studio, nextMilestone);
                })
                .OfType<SupportSummaryMilestone>()
                .Where(item => MatchesSearch(
                    item.Studio.StudioName,
                    item.Studio.ProjectName,
                    item.Milestone.Milestone,
                    item.Milestone.Description,
                    item.Milestone.Developer))
                .ToList();
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidDataException or InvalidOperationException)
        {
            MilestoneErrorMessage = exception.Message;
        }
    }

    private async Task LoadEffortAsync(CancellationToken cancellationToken)
    {
        if (!StartDate.HasValue || !EndDate.HasValue) return;
        try
        {
            var report = await worklogEffortService.GetActualEffortAsync(
                StartDate.Value,
                EndDate.Value,
                User.Identity?.Name ?? string.Empty,
                User.IsInRole("Privileged"),
                cancellationToken);
            EffortSummary = report.EffortSummary;
            StudioEffortBreakdown = report.StudioBreakdown;
            EmployeeEffortBreakdowns = report.EmployeeEffortBreakdowns;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or InvalidDataException
            or ArgumentException)
        {
            EffortErrorMessage = exception.Message;
        }
    }

    private async Task LoadJiraAsync(CancellationToken cancellationToken)
    {
        var tickets = new List<SupportSummaryTicket>();
        var messages = new List<SupportSummaryMessage>();
        foreach (var studio in Studios)
        {
            try
            {
                var result = await jiraTicketService.GetTicketsAsync(new StudioJiraTicketQuery
                {
                    StudioId = studio.Id,
                    RequestingUserId = User.Identity?.Name ?? string.Empty,
                    AllowPrivilegedDefaultCredential = User.IsInRole("Privileged"),
                    ActiveSprintOnly = false,
                    StartDate = StartDate,
                    EndDate = EndDate,
                    UseSprintDateRange = true
                }, cancellationToken);
                IsUsingSharedJiraCredential |= result.IsUsingSharedCredential;
                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    messages.Add(new SupportSummaryMessage(studio, result.Message));
                }
                foreach (var group in result.Groups)
                {
                    tickets.AddRange(group.Tickets.Select(ticket =>
                        new SupportSummaryTicket(studio, group.Name, ticket)));
                }
            }
            catch (HttpRequestException exception)
            {
                messages.Add(new SupportSummaryMessage(studio, exception.Message));
            }
        }

        JiraMessages = messages;
        JiraTickets = tickets
            .Where(item => MatchesSearch(
                item.Studio.StudioName,
                item.Studio.ProjectName,
                item.SupportType,
                item.Ticket.TicketId,
                item.Ticket.Summary,
                item.Ticket.AssignedUser,
                item.Ticket.Status,
                item.Ticket.Priority,
                string.Join(' ', item.Ticket.Components)))
            .OrderBy(item => item.Studio.GroupDisplayOrder ?? int.MaxValue)
            .ThenBy(item => item.Studio.StudioGroup, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Studio.StudioDisplayOrder ?? int.MaxValue)
            .ThenBy(item => item.Studio.StudioName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SupportType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Ticket.TicketId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task LoadConfluenceAsync(CancellationToken cancellationToken)
    {
        try
        {
            ConfluenceResult = await confluenceUpdateService.GetConsolidatedUpdatesAsync(
                new ConsolidatedStudioConfluenceUpdateQuery
                {
                    StudioIds = Studios.Select(studio => studio.Id).ToList(),
                    RequestingUserId = User.Identity?.Name ?? string.Empty,
                    AllowPrivilegedDefaultCredential = User.IsInRole("Privileged"),
                    StartDate = StartDate,
                    EndDate = EndDate
                }, cancellationToken);
            ConfluenceResult.Updates = ConfluenceResult.Updates
                .Where(update => MatchesSearch(
                    GetStudioName(update.StudioId),
                    GetStudioGroup(update.StudioId),
                    update.PageTitle,
                    update.StudioIdentifier,
                    update.Overview,
                    update.StudioWork,
                    update.HpgdsSupport,
                    update.WmdSupport,
                    update.ActionItems,
                    update.Notes))
                .OrderByDescending(update => update.WeekStart)
                .ThenBy(update => FindStudio(update.StudioId)?.GroupDisplayOrder ?? int.MaxValue)
                .ThenBy(update => GetStudioGroup(update.StudioId), StringComparer.OrdinalIgnoreCase)
                .ThenBy(update => FindStudio(update.StudioId)?.StudioDisplayOrder ?? int.MaxValue)
                .ThenBy(update => GetStudioName(update.StudioId), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (HttpRequestException exception)
        {
            ConfluenceErrorMessage = exception.Message;
        }
    }

    private bool MatchesSearch(params string?[] values)
    {
        var search = Search?.Trim();
        return string.IsNullOrWhiteSpace(search)
            || values.Any(value => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }

    private StudioDetails? FindStudio(string studioId) =>
        Studios.FirstOrDefault(studio => string.Equals(studio.Id, studioId, StringComparison.OrdinalIgnoreCase));

    private async Task<string?> GetConfiguredProjectAsync(
        string? projectName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectName)) return null;
        var studios = await studioDirectoryService.GetStudiosAsync(cancellationToken);
        return studios
            .Where(studio => studio.IsActive)
            .Select(studio => studio.ProjectName)
            .FirstOrDefault(project => string.Equals(project, projectName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private IActionResult PlanningError(string message, string fragment)
    {
        ErrorMessage = message;
        return RedirectToPlanningTable(fragment);
    }

    private IActionResult RedirectToPlanningTable(string fragment) =>
        Redirect($"/SupportSummary#{fragment}");

    private void SetReportRange()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        ReportPeriod = string.Equals(ReportPeriod, MonthlyPeriod, StringComparison.OrdinalIgnoreCase)
            ? MonthlyPeriod
            : string.Equals(ReportPeriod, CustomPeriod, StringComparison.OrdinalIgnoreCase)
                ? CustomPeriod
                : WeeklyPeriod;
        var currentWeekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        if (ReportPeriod == CustomPeriod)
        {
            StartDate ??= currentWeekStart;
            EndDate ??= StartDate.Value.AddDays(4);
            SelectedMonth ??= today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        }
        var monthStart = DateOnly.TryParseExact(
            $"{SelectedMonth}-01",
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsedMonth)
                ? parsedMonth
                : new DateOnly(today.Year, today.Month, 1);
        SelectedMonth = monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        WeekOptions = BuildWeekOptions(monthStart, monthEnd);

        if (ReportPeriod == CustomPeriod) return;

        if (IsMonthlyReport)
        {
            StartDate = monthStart;
            EndDate = monthEnd;
            return;
        }

        var defaultWeek = monthStart.Year == today.Year && monthStart.Month == today.Month
            ? WeekOptions.First(option => option.StartDate == currentWeekStart)
            : WeekOptions[0];
        var selectedWeek = WeekOptions.FirstOrDefault(option => option.Number == Week) ?? defaultWeek;
        Week = selectedWeek.Number;
        StartDate = selectedWeek.StartDate;
        EndDate = selectedWeek.EndDate;
    }

    private static IReadOnlyList<SupportSummaryWeekOption> BuildWeekOptions(DateOnly monthStart, DateOnly monthEnd)
    {
        var firstWeekStart = monthStart.AddDays(-(((int)monthStart.DayOfWeek + 6) % 7));
        var options = new List<SupportSummaryWeekOption>();
        for (var weekStart = firstWeekStart; weekStart <= monthEnd; weekStart = weekStart.AddDays(7))
        {
            var weekEnd = weekStart.AddDays(4);
            var number = options.Count + 1;
            options.Add(new SupportSummaryWeekOption(
                number,
                weekStart,
                weekEnd,
                $"Week {number}: {weekStart:dd MMM} - {weekEnd:dd MMM}"));
        }
        return options;
    }

    private static bool IsProjectMilestone(string projectName, MilestoneDto milestone) =>
        string.Equals(milestone.Title, projectName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(milestone.Program, projectName, StringComparison.OrdinalIgnoreCase);

    private static readonly string[] EffortColors =
    [
        "#287565",
        "#c05a35",
        "#d89a2b",
        "#3d7ea6",
        "#7a5ca3",
        "#4d9b72",
        "#b24d72",
        "#687a73",
        "#9a6b3f",
        "#4b638c",
        "#8b6f47",
        "#5f8f91"
    ];
}

public sealed record SupportSummaryMilestone(StudioDetails Studio, MilestoneDto Milestone);
public sealed record SupportSummaryMilestoneDescriptionOption(string ProjectName, string Description);
public sealed record SupportSummaryTicket(StudioDetails Studio, string SupportType, StudioJiraTicket Ticket);
public sealed record SupportSummaryMessage(StudioDetails Studio, string Message);
public sealed record SupportSummaryWeekOption(int Number, DateOnly StartDate, DateOnly EndDate, string Label);
