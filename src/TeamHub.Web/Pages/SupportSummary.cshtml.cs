using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Milestones;
using TeamHub.Studio;

namespace TeamHub.Web.Pages;

public sealed class SupportSummaryModel(
    IStudioDirectoryService studioDirectoryService,
    IMilestoneTrackerService milestoneTrackerService,
    IStudioJiraTicketService jiraTicketService,
    IStudioConfluenceUpdateService confluenceUpdateService) : PageModel
{
    public const string WeeklyPeriod = "Weekly";
    public const string MonthlyPeriod = "Monthly";
    public const string CustomPeriod = "Custom";

    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string ReportPeriod { get; set; } = WeeklyPeriod;
    [BindProperty(SupportsGet = true)] public string? SelectedMonth { get; set; }
    [BindProperty(SupportsGet = true)] public int? Week { get; set; }
    [BindProperty(SupportsGet = true)] public bool ActiveSprintOnly { get; set; } = true;
    [BindProperty(SupportsGet = true)] public DateOnly? StartDate { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? EndDate { get; set; }
    public IReadOnlyList<SupportSummaryWeekOption> WeekOptions { get; private set; } = [];
    public bool IsMonthlyReport => ReportPeriod == MonthlyPeriod;

    public IReadOnlyList<StudioDetails> Studios { get; private set; } = [];
    public IReadOnlyList<SupportSummaryMilestone> ActiveMilestones { get; private set; } = [];
    public IReadOnlyList<SupportSummaryTicket> JiraTickets { get; private set; } = [];
    public IReadOnlyList<SupportSummaryMessage> JiraMessages { get; private set; } = [];
    public StudioConfluenceUpdateResult ConfluenceResult { get; private set; } = new();
    public string? MilestoneErrorMessage { get; private set; }
    public string? ConfluenceErrorMessage { get; private set; }
    public bool IsUsingSharedJiraCredential { get; private set; }

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
        if (Studios.Count == 0) return;

        await LoadMilestonesAsync(cancellationToken);
        await LoadJiraAsync(cancellationToken);
        await LoadConfluenceAsync(cancellationToken);
    }

    public string GetStudioName(string studioId) =>
        FindStudio(studioId)?.StudioName ?? studioId;

    public string GetStudioGroup(string studioId) =>
        FindStudio(studioId)?.StudioGroup ?? "Ungrouped";

    private async Task LoadMilestonesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var today = DateTime.Today;
            var milestones = await milestoneTrackerService.GetMilestonesAsync(cancellationToken);
            ActiveMilestones = Studios
                .Select(studio =>
                {
                    var nextMilestone = milestones
                        .Where(milestone => IsProjectMilestone(studio.ProjectName, milestone))
                        .Where(milestone => !milestone.Milestone.StartsWith("Total MS", StringComparison.OrdinalIgnoreCase))
                        .Where(milestone => !milestone.DeliveryDate.HasValue || milestone.DeliveryDate.Value.Date >= today)
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
                    ActiveSprintOnly = ActiveSprintOnly,
                    StartDate = StartDate,
                    EndDate = EndDate
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
}

public sealed record SupportSummaryMilestone(StudioDetails Studio, MilestoneDto Milestone);
public sealed record SupportSummaryTicket(StudioDetails Studio, string SupportType, StudioJiraTicket Ticket);
public sealed record SupportSummaryMessage(StudioDetails Studio, string Message);
public sealed record SupportSummaryWeekOption(int Number, DateOnly StartDate, DateOnly EndDate, string Label);
