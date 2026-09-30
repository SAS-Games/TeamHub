using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Authentication;
using TeamHub.Milestones;
using TeamHub.Studio;
using TeamHub.Web.Pages;
using TeamHub.Web.WorklogAnalytics;

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
                Description = "Completed delivery description",
                DeliveryDate = DateTime.Today.AddDays(-1)
            }
        ]);
        var jiraService = new StubJiraService();
        var confluenceService = new StubConfluenceService();
        var effortService = new StubWorklogEffortService
        {
            Report = new WorklogEffortReport
            {
                EffortSummary =
                [
                    new WorklogEffortSlice("IHP", 30),
                    new WorklogEffortSlice("Internal Activities", 10)
                ],
                StudioBreakdown =
                [
                    new WorklogEffortSlice("Alpha Project", 30)
                ],
                EmployeeEffortBreakdowns =
                [
                    new WorklogEmployeeEffort(
                        "Asha",
                        [
                            new WorklogEffortSlice("Studio Support", 24),
                            new WorklogEffortSlice("Project Activities", 6)
                        ])
                ]
            }
        };
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "reader@example.com")], "Test");
        var model = new SupportSummaryModel(
            new StubStudioDirectoryService([inactiveStudio, activeStudio]),
            milestoneService,
            jiraService,
            confluenceService,
            new StubPlanningService(),
            effortService)
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
        await model.OnGetEffortAsync(CancellationToken.None);

        var today = DateOnly.FromDateTime(DateTime.Today);
        model.GetBuildAgeingDays(today.AddDays(-3)).Should().Be(3);
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        model.StartDate.Should().Be(weekStart);
        model.EndDate.Should().Be(weekStart.AddDays(4));
        model.ReportPeriod.Should().Be(SupportSummaryModel.WeeklyPeriod);
        model.SelectedMonth.Should().Be(today.ToString("yyyy-MM"));
        model.Week.Should().NotBeNull();
        model.Studios.Should().ContainSingle().Which.Id.Should().Be(activeStudio.Id);
        model.ActiveMilestones.Should().ContainSingle()
            .Which.Milestone.Milestone.Should().Be("Current delivery");
        model.MilestoneDescriptionOptions.Should().Contain(option =>
            option.ProjectName == activeStudio.ProjectName
            && option.Description == "Completed delivery description");
        model.JiraTickets.Select(item => item.SupportType).Should().Equal("Direct Support", "Indirect Support");
        model.JiraTickets.Select(item => item.Ticket.AssignedUser).Should().Equal("Asha", "Dev");

        jiraService.Queries.Should().ContainSingle();
        jiraService.Queries[0].StudioId.Should().Be(activeStudio.Id);
        jiraService.Queries[0].ActiveSprintOnly.Should().BeFalse();
        jiraService.Queries[0].UseSprintDateRange.Should().BeTrue();
        jiraService.Queries[0].StartDate.Should().Be(weekStart);
        jiraService.Queries[0].EndDate.Should().Be(weekStart.AddDays(4));
        confluenceService.LastQuery.Should().NotBeNull();
        confluenceService.LastQuery!.StudioIds.Should().Equal(activeStudio.Id);
        confluenceService.LastQuery.StartDate.Should().Be(weekStart);
        confluenceService.LastQuery.EndDate.Should().Be(weekStart.AddDays(4));
        model.ConfluenceResult.Updates.Should().ContainSingle().Which.StudioId.Should().Be(activeStudio.Id);
        effortService.StartDate.Should().Be(weekStart);
        effortService.EndDate.Should().Be(weekStart.AddDays(4));
        effortService.RequestingUserId.Should().Be("reader@example.com");
        effortService.AllowPrivilegedDefaultCredential.Should().BeFalse();
        model.EffortSummary.Sum(slice => slice.Hours).Should().Be(40);
        model.StudioEffortBreakdown.Should().ContainSingle()
            .Which.Label.Should().Be(activeStudio.ProjectName);
        model.EmployeeEffortBreakdowns.Should().ContainSingle()
            .Which.Employee.Should().Be("Asha");
        model.EmployeeEffortBreakdowns[0].Slices.Sum(slice => slice.Hours).Should().Be(30);
        model.BuildPieGradient(model.EffortSummary).Should().StartWith("conic-gradient(");
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
            confluenceService,
            new StubPlanningService(),
            new StubWorklogEffortService())
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
        jiraService.Queries[0].UseSprintDateRange.Should().BeTrue();
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
            confluenceService,
            new StubPlanningService(),
            new StubWorklogEffortService())
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
        jiraService.Queries[0].UseSprintDateRange.Should().BeTrue();
        jiraService.Queries[0].StartDate.Should().Be(new DateOnly(2026, 9, 3));
        jiraService.Queries[0].EndDate.Should().Be(new DateOnly(2026, 9, 18));
        confluenceService.LastQuery!.StartDate.Should().Be(new DateOnly(2026, 9, 3));
        confluenceService.LastQuery.EndDate.Should().Be(new DateOnly(2026, 9, 18));
    }

    [Theory]
    [InlineData(false, "Something went wrong while loading logged effort. Please contact an administrator.")]
    [InlineData(true, "No Python at C:\\Users\\developer\\python.exe")]
    public async Task OnGetAsync_ShowsDetailedEffortErrorsOnlyToAdministrators(
        bool isAdmin,
        string expectedDisplayMessage)
    {
        const string diagnostic = "No Python at C:\\Users\\developer\\python.exe";
        var studio = Studio("active", "Alpha Studio", "Alpha Project", isActive: true);
        var claims = new List<Claim> { new(ClaimTypes.Name, "user@example.com") };
        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, TeamHubUserTypes.Admin));
        }

        var model = new SupportSummaryModel(
            new StubStudioDirectoryService([studio]),
            new StubMilestoneService([]),
            new StubJiraService(),
            new StubConfluenceService(),
            new StubPlanningService(),
            new StubWorklogEffortService { Error = new InvalidOperationException(diagnostic) })
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
                }
            }
        };

        await model.OnGetAsync(CancellationToken.None);
        await model.OnGetEffortAsync(CancellationToken.None);

        model.EffortErrorMessage.Should().Be(diagnostic);
        model.EffortErrorDisplayMessage.Should().Be(expectedDisplayMessage);
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
            new StubConfluenceService(),
            new StubPlanningService(),
            new StubWorklogEffortService())
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

    private sealed class StubPlanningService : ISupportSummaryPlanningService
    {
        public List<ExpectedMilestoneDelivery> ExpectedDeliveries { get; } = [];
        public List<MilestoneBuildReview> BuildReviews { get; } = [];

        public Task<IReadOnlyList<ExpectedMilestoneDelivery>> GetExpectedDeliveriesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExpectedMilestoneDelivery>>(ExpectedDeliveries);

        public Task<ExpectedMilestoneDelivery> SaveExpectedDeliveryAsync(
            ExpectedMilestoneDelivery delivery,
            CancellationToken cancellationToken = default)
        {
            ExpectedDeliveries.RemoveAll(item => item.Id == delivery.Id);
            ExpectedDeliveries.Add(delivery);
            return Task.FromResult(delivery);
        }

        public Task DeleteExpectedDeliveryAsync(Guid id, CancellationToken cancellationToken = default)
        {
            ExpectedDeliveries.RemoveAll(item => item.Id == id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MilestoneBuildReview>> GetBuildReviewsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MilestoneBuildReview>>(BuildReviews);

        public Task<MilestoneBuildReview> SaveBuildReviewAsync(
            MilestoneBuildReview review,
            CancellationToken cancellationToken = default)
        {
            BuildReviews.RemoveAll(item => item.Id == review.Id);
            BuildReviews.Add(review);
            return Task.FromResult(review);
        }

        public Task DeleteBuildReviewAsync(Guid id, CancellationToken cancellationToken = default)
        {
            BuildReviews.RemoveAll(item => item.Id == id);
            return Task.CompletedTask;
        }
    }

    private sealed class StubWorklogEffortService : IWorklogEffortService
    {
        public DateOnly? StartDate { get; private set; }
        public DateOnly? EndDate { get; private set; }
        public string? RequestingUserId { get; private set; }
        public bool? AllowPrivilegedDefaultCredential { get; private set; }
        public WorklogEffortReport Report { get; set; } = new();
        public Exception? Error { get; set; }

        public Task<WorklogEffortReport> GetActualEffortAsync(
            DateOnly startDate,
            DateOnly endDate,
            string requestingUserId,
            bool allowPrivilegedDefaultCredential,
            CancellationToken cancellationToken = default)
        {
            StartDate = startDate;
            EndDate = endDate;
            RequestingUserId = requestingUserId;
            AllowPrivilegedDefaultCredential = allowPrivilegedDefaultCredential;
            if (Error is not null) throw Error;
            return Task.FromResult(Report);
        }
    }
}
