using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Milestones;
using TeamHub.Studio;
using TeamHub.Web.Pages;

namespace TeamHub.Tests;

public sealed class SupportSummaryModelTests
{
    [Fact]
    public async Task OnGetAsync_LoadsCurrentWeekSupportAcrossAllActiveStudios()
    {
        var activeStudio = Studio("active", "Alpha Studio", "Alpha Project", isActive: true);
        var inactiveStudio = Studio("inactive", "Old Studio", "Old Project", isActive: false);
        var milestoneService = new StubMilestoneService(
        [
            new()
            {
                Title = activeStudio.ProjectName,
                Milestone = "Current delivery",
                Description = "Ship the support update",
                Developer = "Dev One",
                DeliveryDate = DateTime.Today.AddDays(7)
            },
            new()
            {
                Title = activeStudio.ProjectName,
                Milestone = "Completed delivery",
                DeliveryDate = DateTime.Today.AddDays(-1)
            }
        ]);
        var jiraService = new StubJiraService();
        var confluenceService = new StubConfluenceService();
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "reader@example.com")], "Test");
        var model = new SupportSummaryModel(
            new StubStudioDirectoryService([inactiveStudio, activeStudio]),
            milestoneService,
            jiraService,
            confluenceService)
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(identity)
                }
            }
        };

        await model.OnGetAsync(CancellationToken.None);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        model.StartDate.Should().Be(weekStart);
        model.EndDate.Should().Be(weekStart.AddDays(4));
        model.ReportPeriod.Should().Be(SupportSummaryModel.WeeklyPeriod);
        model.SelectedMonth.Should().Be(today.ToString("yyyy-MM"));
        model.Week.Should().NotBeNull();
        model.ActiveSprintOnly.Should().BeTrue();
        model.Studios.Should().ContainSingle().Which.Id.Should().Be(activeStudio.Id);
        model.ActiveMilestones.Should().ContainSingle()
            .Which.Milestone.Milestone.Should().Be("Current delivery");
        model.JiraTickets.Select(item => item.SupportType).Should().Equal("Direct Support", "Indirect Support");
        model.JiraTickets.Select(item => item.Ticket.AssignedUser).Should().Equal("Asha", "Dev");

        jiraService.Queries.Should().ContainSingle();
        jiraService.Queries[0].StudioId.Should().Be(activeStudio.Id);
        jiraService.Queries[0].ActiveSprintOnly.Should().BeTrue();
        jiraService.Queries[0].StartDate.Should().Be(weekStart);
        jiraService.Queries[0].EndDate.Should().Be(weekStart.AddDays(4));
        confluenceService.LastQuery.Should().NotBeNull();
        confluenceService.LastQuery!.StudioIds.Should().Equal(activeStudio.Id);
        confluenceService.LastQuery.StartDate.Should().Be(weekStart);
        confluenceService.LastQuery.EndDate.Should().Be(weekStart.AddDays(4));
        model.ConfluenceResult.Updates.Should().ContainSingle().Which.StudioId.Should().Be(activeStudio.Id);
    }

    [Fact]
    public async Task OnGetAsync_MonthlyPeriodUsesTheFullSelectedCalendarMonth()
    {
        var studio = Studio("active", "Alpha Studio", "Alpha Project", isActive: true);
        var jiraService = new StubJiraService();
        var confluenceService = new StubConfluenceService();
        var model = new SupportSummaryModel(
            new StubStudioDirectoryService([studio]),
            new StubMilestoneService([]),
            jiraService,
            confluenceService)
        {
            ReportPeriod = SupportSummaryModel.MonthlyPeriod,
            SelectedMonth = "2026-09",
            Week = 3,
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() }
        };

        await model.OnGetAsync(CancellationToken.None);

        model.IsMonthlyReport.Should().BeTrue();
        model.StartDate.Should().Be(new DateOnly(2026, 9, 1));
        model.EndDate.Should().Be(new DateOnly(2026, 9, 30));
        jiraService.Queries.Should().ContainSingle();
        jiraService.Queries[0].StartDate.Should().Be(new DateOnly(2026, 9, 1));
        jiraService.Queries[0].EndDate.Should().Be(new DateOnly(2026, 9, 30));
        confluenceService.LastQuery!.StartDate.Should().Be(new DateOnly(2026, 9, 1));
        confluenceService.LastQuery.EndDate.Should().Be(new DateOnly(2026, 9, 30));
    }

    [Fact]
    public async Task OnGetAsync_CustomPeriodPreservesTheExplicitDateRange()
    {
        var studio = Studio("active", "Alpha Studio", "Alpha Project", isActive: true);
        var jiraService = new StubJiraService();
        var confluenceService = new StubConfluenceService();
        var model = new SupportSummaryModel(
            new StubStudioDirectoryService([studio]),
            new StubMilestoneService([]),
            jiraService,
            confluenceService)
        {
            ReportPeriod = SupportSummaryModel.CustomPeriod,
            StartDate = new DateOnly(2026, 9, 3),
            EndDate = new DateOnly(2026, 9, 18),
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() }
        };

        await model.OnGetAsync(CancellationToken.None);

        model.ReportPeriod.Should().Be(SupportSummaryModel.CustomPeriod);
        model.StartDate.Should().Be(new DateOnly(2026, 9, 3));
        model.EndDate.Should().Be(new DateOnly(2026, 9, 18));
        jiraService.Queries[0].StartDate.Should().Be(new DateOnly(2026, 9, 3));
        jiraService.Queries[0].EndDate.Should().Be(new DateOnly(2026, 9, 18));
        confluenceService.LastQuery!.StartDate.Should().Be(new DateOnly(2026, 9, 3));
        confluenceService.LastQuery.EndDate.Should().Be(new DateOnly(2026, 9, 18));
    }

    [Fact]
    public async Task OnGetAsync_LoadsOnlyTheNextMilestonePerStudioInConfiguredStudioOrder()
    {
        var secondStudio = Studio("second", "Second Studio", "Second Project", isActive: true);
        secondStudio.GroupDisplayOrder = 1;
        secondStudio.StudioDisplayOrder = 2;
        var firstStudio = Studio("first", "First Studio", "First Project", isActive: true);
        firstStudio.GroupDisplayOrder = 1;
        firstStudio.StudioDisplayOrder = 1;
        var thirdStudio = Studio("third", "Third Studio", "Third Project", isActive: true);
        thirdStudio.GroupDisplayOrder = 2;
        thirdStudio.StudioDisplayOrder = 1;
        var milestones = new StubMilestoneService(
        [
            new() { Title = firstStudio.ProjectName, Milestone = "Later first milestone", DeliveryDate = DateTime.Today.AddDays(10) },
            new() { Title = firstStudio.ProjectName, Milestone = "Next first milestone", DeliveryDate = DateTime.Today.AddDays(3) },
            new() { Title = firstStudio.ProjectName, Milestone = "Past first milestone", DeliveryDate = DateTime.Today.AddDays(-1) },
            new() { Title = secondStudio.ProjectName, Milestone = "Next second milestone", DeliveryDate = DateTime.Today.AddDays(1) },
            new() { Title = secondStudio.ProjectName, Milestone = "Total MS second", DeliveryDate = DateTime.Today },
            new() { Title = thirdStudio.ProjectName, Milestone = "Unscheduled third milestone" }
        ]);
        var model = new SupportSummaryModel(
            new StubStudioDirectoryService([thirdStudio, secondStudio, firstStudio]),
            milestones,
            new StubJiraService(),
            new StubConfluenceService())
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() }
        };

        await model.OnGetAsync(CancellationToken.None);

        model.Studios.Select(studio => studio.Id).Should().Equal("first", "second", "third");
        model.ActiveMilestones.Select(item => item.Studio.Id).Should().Equal("first", "second", "third");
        model.ActiveMilestones.Select(item => item.Milestone.Milestone).Should().Equal(
            "Next first milestone",
            "Next second milestone",
            "Unscheduled third milestone");
    }

    private static StudioDetails Studio(string id, string name, string project, bool isActive) => new()
    {
        Id = id,
        StudioName = name,
        ProjectName = project,
        StudioGroup = "Group",
        IsActive = isActive
    };

    private sealed class StubStudioDirectoryService(IReadOnlyList<StudioDetails> studios) : IStudioDirectoryService
    {
        public Task<IReadOnlyList<StudioDetails>> GetStudiosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(studios);

        public Task<StudioDetails?> GetStudioAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(studios.FirstOrDefault(studio => studio.Id == id));

        public Task<StudioDetails> SaveStudioAsync(StudioDetails studio, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteStudioAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubMilestoneService(IReadOnlyList<MilestoneDto> milestones) : IMilestoneTrackerService
    {
        public Task<IReadOnlyList<MilestoneDto>> GetMilestonesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(milestones);

        public Task<MilestoneSourceTestResult> TestSourceAsync(
            MilestoneSourceSettings settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubJiraService : IStudioJiraTicketService
    {
        public List<StudioJiraTicketQuery> Queries { get; } = [];

        public Task<StudioJiraTicketResult> GetTicketsAsync(
            StudioJiraTicketQuery query,
            CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return Task.FromResult(new StudioJiraTicketResult
            {
                IsConfigured = true,
                Groups =
                [
                    new StudioJiraTicketGroup
                    {
                        Name = "Direct Support",
                        Tickets = [Ticket("SUP-1", "Direct issue", "Asha")]
                    },
                    new StudioJiraTicketGroup
                    {
                        Name = "Indirect Support",
                        Tickets = [Ticket("SUP-2", "Indirect issue", "Dev")]
                    }
                ]
            });
        }

        private static StudioJiraTicket Ticket(string id, string summary, string assignee) => new()
        {
            TicketId = id,
            Summary = summary,
            AssignedUser = assignee,
            Status = "In Progress",
            Priority = "High"
        };
    }

    private sealed class StubConfluenceService : IStudioConfluenceUpdateService
    {
        public ConsolidatedStudioConfluenceUpdateQuery? LastQuery { get; private set; }

        public Task<StudioConfluenceUpdateResult> GetUpdatesAsync(
            StudioConfluenceUpdateQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StudioConfluenceUpdateResult> GetConsolidatedUpdatesAsync(
            ConsolidatedStudioConfluenceUpdateQuery query,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(new StudioConfluenceUpdateResult
            {
                IsConfigured = true,
                Updates = query.StudioIds
                    .Select(studioId => new StudioConfluenceWeeklyUpdate
                    {
                        StudioId = studioId,
                        WeekStart = query.StartDate!.Value,
                        WeekEnd = query.EndDate!.Value,
                        PageFound = true,
                        StudioFound = true
                    })
                    .ToList()
            });
        }
    }
}
