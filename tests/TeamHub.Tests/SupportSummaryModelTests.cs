using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.AI.Contracts;
using TeamHub.AI.ReportGenerator.Contracts;
using TeamHub.Web.AccessControl;
using TeamHub.Web.Reports;
using TeamHub.Authentication;
using TeamHub.Milestones;
using TeamHub.Studio;
using TeamHub.Web.Pages;
using TeamHub.Web.WorklogAnalytics;

namespace TeamHub.Tests;

public sealed class SupportSummaryModelTests
{
    [Fact]
    public async Task OnPostSaveConfluenceUpdateAsync_SendsPersonalEditAndReturnsToConfluenceSection()
    {
        var confluenceService = new StubConfluenceService();
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "editor@example.com")], "Test");
        var model = new SupportSummaryModel(
            new StubStudioDirectoryService([]),
            new StubMilestoneService([]),
            new StubJiraService(),
            confluenceService,
            new StubPlanningService(),
            new StubWorklogEffortService(),
            new StubWeeklySummaryService(),
            new StubCurrentAccess(
                AccessLevel.ReadOnly,
                AccessLevel.Edit,
                TeamHubUserTypes.Registered))
        {
            ReportPeriod = SupportSummaryModel.CustomPeriod,
            StartDate = new DateOnly(2026, 9, 14),
            EndDate = new DateOnly(2026, 9, 18),
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };

        var action = await model.OnPostSaveConfluenceUpdateAsync(
            "studio-1",
            "page-1",
            new DateOnly(2026, 9, 14),
            8,
            "Work",
            "HPGDS",
            "WMD",
            "Action",
            "Notes",
            CancellationToken.None);

        confluenceService.LastUpdateCommand.Should().NotBeNull();
        confluenceService.LastUpdateCommand!.RequestingUserId.Should().Be("editor@example.com");
        confluenceService.LastUpdateCommand.PageId.Should().Be("page-1");
        confluenceService.LastUpdateCommand.ExpectedPageVersion.Should().Be(8);
        confluenceService.LastUpdateCommand.Notes.Should().Be("Notes");
        model.StatusMessage.Should().Be("Saved.");
        action.Should().BeOfType<RedirectResult>().Which.Url.Should().EndWith("#confluence");
    }

    [Theory]
    [InlineData(AccessLevel.ReadOnly, false)]
    [InlineData(AccessLevel.Edit, true)]
    [InlineData(AccessLevel.FullAccess, true)]
    public async Task OnGetAsync_ExposesConfluenceEditingFromSupportSummaryPermission(
        AccessLevel studioSupportAccess,
        bool expected)
    {
        var model = new SupportSummaryModel(
            new StubStudioDirectoryService([]),
            new StubMilestoneService([]),
            new StubJiraService(),
            new StubConfluenceService(),
            new StubPlanningService(),
            new StubWorklogEffortService(),
            new StubWeeklySummaryService(),
            new StubCurrentAccess(
                AccessLevel.ReadOnly,
                studioSupportAccess,
                TeamHubUserTypes.Registered))
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() }
        };

        await model.OnGetAsync(CancellationToken.None);

        model.CanEditConfluence.Should().Be(expected);
    }

    [Fact]
    public async Task OnPostSaveConfluenceUpdateAsync_RejectsUsersWithoutSupportSummaryEditPermission()
    {
        var confluenceService = new StubConfluenceService();
        var model = new SupportSummaryModel(
            new StubStudioDirectoryService([]),
            new StubMilestoneService([]),
            new StubJiraService(),
            confluenceService,
            new StubPlanningService(),
            new StubWorklogEffortService(),
            new StubWeeklySummaryService(),
            new StubCurrentAccess(
                AccessLevel.ReadOnly,
                AccessLevel.ReadOnly,
                TeamHubUserTypes.Registered))
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() }
        };

        var action = await model.OnPostSaveConfluenceUpdateAsync(
            "studio-1",
            "page-1",
            new DateOnly(2026, 9, 14),
            8,
            "Work",
            "HPGDS",
            "WMD",
            "Action",
            "Notes",
            CancellationToken.None);

        action.Should().BeOfType<ForbidResult>();
        confluenceService.LastUpdateCommand.Should().BeNull();
    }

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
            effortService,
            new StubWeeklySummaryService(),
            new StubCurrentAccess(AccessLevel.FullAccess))
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
            new StubWorklogEffortService(),
            new StubWeeklySummaryService(),
            new StubCurrentAccess(AccessLevel.FullAccess))
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
            new StubWorklogEffortService(),
            new StubWeeklySummaryService(),
            new StubCurrentAccess(AccessLevel.FullAccess))
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
            new StubWorklogEffortService { Error = new InvalidOperationException(diagnostic) },
            new StubWeeklySummaryService(),
            new StubCurrentAccess(AccessLevel.FullAccess))
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
            new StubWorklogEffortService(),
            new StubWeeklySummaryService(),
            new StubCurrentAccess(AccessLevel.FullAccess))
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

    [Fact]
    public async Task OnGetAsync_UsesEachProjectsFinalReleaseDateForItsCurrentMilestone()
    {
        var alphaStudio = Studio("alpha", "Alpha Studio", "Alpha Project", isActive: true);
        var betaStudio = Studio("beta", "Beta Studio", "Beta Project", isActive: true);
        var alphaReleaseDate = DateTime.Today.AddDays(90);
        var betaReleaseDate = DateTime.Today.AddDays(60);
        var model = new SupportSummaryModel(
            new StubStudioDirectoryService([alphaStudio, betaStudio]),
            new StubMilestoneService(
            [
                new()
                {
                    Title = alphaStudio.ProjectName,
                    Milestone = "Gold Master",
                    DeliveryDate = DateTime.Today.AddDays(10)
                },
                new()
                {
                    Title = alphaStudio.ProjectName,
                    Milestone = "Release",
                    DeliveryDate = alphaReleaseDate,
                    ReleaseDate = alphaReleaseDate
                },
                new()
                {
                    Title = betaStudio.ProjectName,
                    Milestone = "Beta",
                    DeliveryDate = DateTime.Today.AddDays(5)
                },
                new()
                {
                    Title = betaStudio.ProjectName,
                    Milestone = "Release",
                    DeliveryDate = betaReleaseDate,
                    ReleaseDate = betaReleaseDate
                }
            ]),
            new StubJiraService(),
            new StubConfluenceService(),
            new StubPlanningService(),
            new StubWorklogEffortService(),
            new StubWeeklySummaryService(),
            new StubCurrentAccess(AccessLevel.FullAccess))
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() }
        };

        await model.OnGetAsync(CancellationToken.None);

        model.ActiveMilestones.Select(item => item.Milestone.Milestone).Should().Equal("Gold Master", "Beta");
        model.ActiveMilestones.Select(item => item.ReleaseDate).Should().Equal(alphaReleaseDate, betaReleaseDate);
    }

    private static StudioDetails Studio(string id, string name, string project, bool isActive) => new()
    {
        Id = id,
        StudioName = name,
        ProjectName = project,
        StudioGroup = "Group",
        IsActive = isActive
    };

    [Fact]
    public async Task WeeklySummaryPost_DeniesUnauthorizedRequestsBeforeReadingSources()
    {
        var summary = new StubWeeklySummaryService { Allowed = false };
        var model = SummaryModel(summary);
        (await model.OnPostGenerateWeeklyWorkSummaryAsync(CancellationToken.None))
            .Should().BeOfType<ForbidResult>();
        summary.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("Monthly", "2026-09", 1)]
    [InlineData("Custom", "2026-09", 1)]
    [InlineData("Weekly", "invalid", 1)]
    [InlineData("Weekly", "9999-12", 1)]
    [InlineData("Weekly", "2026-09", 99)]
    public async Task WeeklySummaryPost_RejectsInvalidPeriodWithoutGeneration(string period, string month, int week)
    {
        var summary = new StubWeeklySummaryService { Allowed = true };
        var model = SummaryModel(summary);
        model.ReportPeriod = period;
        model.SelectedMonth = month;
        model.Week = week;
        var result = (JsonResult)await model.OnPostGenerateWeeklyWorkSummaryAsync(CancellationToken.None);
        result.StatusCode.Should().Be(400);
        summary.Calls.Should().Be(0);
    }

    [Fact]
    public async Task WeeklySummaryPost_UsesSelectedWeekIncludingPreviousMonthBoundary()
    {
        var summary = new StubWeeklySummaryService { Allowed = true };
        var model = SummaryModel(summary);
        model.Search = "a presentation filter";
        var result = await model.OnPostGenerateWeeklyWorkSummaryAsync(CancellationToken.None);
        result.Should().BeOfType<JsonResult>();
        summary.Start.Should().Be(new DateOnly(2026, 8, 31));
        summary.End.Should().Be(new DateOnly(2026, 9, 4));
        model.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    [Fact]
    public async Task WeeklySummaryPost_SurfacesGenerationFailure()
    {
        var model = SummaryModel(new StubWeeklySummaryService
        {
            Allowed = true,
            Error = new WeeklyWorkSummaryException("AI report generation is busy.")
        });
        var result = (JsonResult)await model.OnPostGenerateWeeklyWorkSummaryAsync(CancellationToken.None);
        result.StatusCode.Should().Be(400);
        System.Text.Json.JsonSerializer.Serialize(result.Value).Should().Contain("AI report generation is busy.");
    }

    [Theory]
    [InlineData(TeamHubUserTypes.Registered, AccessLevel.NoAccess, true, false)]
    [InlineData(TeamHubUserTypes.Registered, AccessLevel.ReadOnly, true, false)]
    [InlineData(TeamHubUserTypes.Registered, AccessLevel.Create, true, true)]
    [InlineData(TeamHubUserTypes.Registered, AccessLevel.Edit, true, true)]
    [InlineData(TeamHubUserTypes.Registered, AccessLevel.Delete, true, true)]
    [InlineData(TeamHubUserTypes.Registered, AccessLevel.FullAccess, true, true)]
    [InlineData(TeamHubUserTypes.Registered, AccessLevel.Create, false, false)]
    [InlineData(TeamHubUserTypes.Privileged, AccessLevel.Create, true, true)]
    [InlineData(TeamHubUserTypes.Privileged, AccessLevel.FullAccess, true, true)]
    [InlineData(TeamHubUserTypes.Admin, AccessLevel.FullAccess, false, false)]
    [InlineData(TeamHubUserTypes.Admin, AccessLevel.NoAccess, true, true)]
    public async Task WeeklySummaryService_RequiresMatrixCreateAccessAndEnabledFeature(
        string userType, AccessLevel granted, bool enabled, bool expected)
    {
        var service = new WeeklyWorkSummaryService(
            new StubCurrentAccess(granted, userType: userType), new StubStudioDirectoryService([]),
            new StubConfluenceService(), new StubWeeklyWorkflow(),
            new StubAiConfiguration(enabled));
        (await service.CanGenerateAsync()).Should().Be(expected);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task WeeklySummaryService_DeniesMissingPermissionOrDisabledFeatureBeforeFetchingSources(
        bool allowed, bool enabled)
    {
        var confluence = new StubConfluenceService();
        var workflow = new StubWeeklyWorkflow();
        var service = new WeeklyWorkSummaryService(
            new StubCurrentAccess(allowed ? AccessLevel.Create : AccessLevel.ReadOnly,
                userType: TeamHubUserTypes.Registered),
            new StubStudioDirectoryService([Studio("one", "One", "One", true)]),
            confluence, workflow, new StubAiConfiguration(enabled));
        Func<Task> act = () => service.GenerateAsync(new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 4),
            new ClaimsPrincipal());
        if (!allowed) await act.Should().ThrowAsync<UnauthorizedAccessException>();
        else await act.Should().ThrowAsync<WeeklyWorkSummaryException>();
        confluence.LastQuery.Should().BeNull();
        workflow.Request.Should().BeNull();
    }

    [Fact]
    public async Task WeeklySummaryService_ReadsUpdatedConfigurationWithoutRecreatingTheService()
    {
        var configuration = new StubAiConfiguration(false);
        var confluence = new StubConfluenceService();
        var service = new WeeklyWorkSummaryService(
            new StubCurrentAccess(AccessLevel.Create), new StubStudioDirectoryService([]),
            confluence, new StubWeeklyWorkflow(), configuration);

        (await service.CanGenerateAsync()).Should().BeFalse();
        configuration.Snapshot.ReportGenerator.Enabled = true;
        (await service.CanGenerateAsync()).Should().BeTrue();
        configuration.Snapshot.ReportGenerator.Enabled = false;
        (await service.CanGenerateAsync()).Should().BeFalse();
        var generate = () => service.GenerateAsync(new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 4),
            new ClaimsPrincipal());
        await generate.Should().ThrowAsync<WeeklyWorkSummaryException>().WithMessage("*Configuration > AI Services*");
        confluence.LastQuery.Should().BeNull();
    }

    [Fact]
    public async Task WeeklySummaryService_DeniesMissingSupportSummaryPermission()
    {
        var service = new WeeklyWorkSummaryService(
            new StubCurrentAccess(AccessLevel.Create, AccessLevel.NoAccess, TeamHubUserTypes.Registered),
            new StubStudioDirectoryService([]), new StubConfluenceService(), new StubWeeklyWorkflow(),
            new StubAiConfiguration(true));
        (await service.CanGenerateAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task WeeklySummaryService_RejectsConfluenceReadFailureWithoutGeneratingPartialReport()
    {
        var workflow = new StubWeeklyWorkflow();
        var service = new WeeklyWorkSummaryService(
            new StubCurrentAccess(AccessLevel.Create),
            new StubStudioDirectoryService([Studio("one", "One", "One", true)]),
            new StubConfluenceService
            {
                Result = new StudioConfluenceUpdateResult { IsConfigured = true, Message = "Confluence could not be read." }
            }, workflow, new StubAiConfiguration(true));
        var act = () => service.GenerateAsync(new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 4),
            new ClaimsPrincipal());
        await act.Should().ThrowAsync<WeeklyWorkSummaryException>().WithMessage("Confluence could not be read.");
        workflow.Request.Should().BeNull();
    }

    [Theory]
    [InlineData(TeamHubUserTypes.Admin, true, true, true)]
    [InlineData(TeamHubUserTypes.Admin, false, true, false)]
    [InlineData(TeamHubUserTypes.Admin, true, false, false)]
    [InlineData(TeamHubUserTypes.Registered, true, true, true)]
    [InlineData(TeamHubUserTypes.Privileged, true, true, true)]
    [InlineData(TeamHubUserTypes.Registered, false, true, false)]
    [InlineData(TeamHubUserTypes.Registered, true, false, false)]
    public async Task WeeklySummaryGet_ExposesSectionOnlyToEnabledAuthorizedUsers(
        string role, bool enabled, bool allowed, bool expected)
    {
        var model = SummaryModel(new StubWeeklySummaryService { Allowed = allowed, IsEnabled = enabled });
        model.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "Test"));
        await model.OnGetAsync(CancellationToken.None);
        model.CanGenerateWeeklyWorkSummary.Should().Be(expected);
    }

    [Theory]
    [InlineData(TeamHubUserTypes.Registered, false)]
    [InlineData(TeamHubUserTypes.Privileged, false)]
    [InlineData(TeamHubUserTypes.Admin, false)]
    public async Task WeeklySummaryPost_DeniesDisabledFeatureEvenWhenMatrixAllowsGeneration(
        string role, bool enabled)
    {
        var summary = new StubWeeklySummaryService { Allowed = true, IsEnabled = enabled };
        var model = SummaryModel(summary);
        model.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "Test"));
        (await model.OnPostGenerateWeeklyWorkSummaryAsync(CancellationToken.None)).Should().BeOfType<ForbidResult>();
        summary.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(TeamHubUserTypes.Registered)]
    [InlineData(TeamHubUserTypes.Privileged)]
    [InlineData(TeamHubUserTypes.Admin)]
    public async Task WeeklySummaryPost_AllowsUsersWithGenerationPermission(string role)
    {
        var summary = new StubWeeklySummaryService { Allowed = true };
        var model = SummaryModel(summary);
        model.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "Test"));
        var result = await model.OnPostGenerateWeeklyWorkSummaryAsync(CancellationToken.None);
        result.Should().BeOfType<JsonResult>().Which.StatusCode.Should().BeNull();
        summary.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(TeamHubUserTypes.Admin)]
    [InlineData(TeamHubUserTypes.Registered)]
    [InlineData(TeamHubUserTypes.Privileged)]
    public async Task WeeklySummaryService_ProjectsOnlyApprovedColumnsAndUsesActiveStudioOrder(string role)
    {
        var second = Studio("second", "Second", "Second project", true);
        second.StudioDisplayOrder = 2;
        var first = Studio("first", "First", "First project", true);
        first.StudioDisplayOrder = 1;
        var confluence = new StubConfluenceService
        {
            Result = new StudioConfluenceUpdateResult
            {
                IsConfigured = true,
                Updates =
                [
                    SummaryUpdate("second"), SummaryUpdate("first"),
                    SummaryUpdate("old", new DateOnly(2026, 8, 24))
                ]
            }
        };
        var workflow = new StubWeeklyWorkflow();
        var service = new WeeklyWorkSummaryService(
            new StubCurrentAccess(AccessLevel.Create, userType: role),
            new StubStudioDirectoryService([second, Studio("inactive", "Inactive", "Inactive", false), first]),
            confluence, workflow, new StubAiConfiguration(true));
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "reader@example.com"), new Claim(ClaimTypes.Role, role)], "Test"));

        var preview = await service.GenerateAsync(new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 4), user);

        confluence.LastQuery!.StudioIds.Should().Equal("first", "second");
        confluence.LastQuery.AllowPrivilegedDefaultCredential.Should().Be(role == TeamHubUserTypes.Privileged);
        confluence.LastQuery.RequestingUserId.Should().Be("reader@example.com");
        workflow.Request!.Sources.Select(source => source.StudioId).Should().Equal("first", "second");
        var payload = System.Text.Json.JsonSerializer.Serialize(workflow.Request.Sources);
        payload.Should().Contain("Support text").And.Contain("Notes text");
        payload.Should().NotContain("Excluded text");
        preview.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task WeeklySummaryService_DisclosesMissingAndEmptySources()
    {
        var confluence = new StubConfluenceService
        {
            Result = new StudioConfluenceUpdateResult
            {
                IsConfigured = true,
                Updates = [SummaryUpdate("present"), SummaryUpdate("empty")]
            }
        };
        var empty = confluence.Result!.Updates[1];
        empty.HpgdsSupport = "";
        empty.Notes = "";
        var workflow = new StubWeeklyWorkflow();
        var service = new WeeklyWorkSummaryService(
            new StubCurrentAccess(AccessLevel.Create),
            new StubStudioDirectoryService([
                Studio("present", "Present", "P", true),
                Studio("empty", "Empty", "E", true),
                Studio("missing", "Missing", "M", true)]),
            confluence, workflow, new StubAiConfiguration(true));
        var preview = await service.GenerateAsync(new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 4),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "reader")], "Test")));
        workflow.Request!.Sources.Should().ContainSingle().Which.StudioId.Should().Be("present");
        preview.Warnings.Should().HaveCount(2);
    }

    private static StudioConfluenceWeeklyUpdate SummaryUpdate(string id, DateOnly? start = null) => new()
    {
        StudioId = id, WeekStart = start ?? new DateOnly(2026, 8, 31),
        WeekEnd = (start ?? new DateOnly(2026, 8, 31)).AddDays(4),
        PageFound = true, StudioFound = true, HpgdsSupport = "Support text", Notes = "Notes text",
        StudioWork = "Excluded text", WmdSupport = "Excluded text", ActionItems = "Excluded text",
        PageId = "123", PageVersion = 2
    };

    private static SupportSummaryModel SummaryModel(StubWeeklySummaryService service) => new(
        new StubStudioDirectoryService([]), new StubMilestoneService([]), new StubJiraService(),
        new StubConfluenceService(), new StubPlanningService(), new StubWorklogEffortService(), service,
        new StubCurrentAccess(AccessLevel.FullAccess))
    {
        SelectedMonth = "2026-09", Week = 1,
        PageContext = new PageContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, TeamHubUserTypes.Admin)], "Test"))
            }
        }
    };

    private sealed class StubAiConfiguration(bool enabled) : IAiConfigurationService
    {
        public AiConfigurationSnapshot Snapshot { get; set; } = new()
        {
            ReportGenerator = new AiReportGeneratorOptions { Enabled = enabled }
        };

        public Task<AiConfigurationSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task SaveAsync(SaveAiConfigurationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string?> GetCredentialAsync(AiProviderSettings settings, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(null);
    }

    private sealed class StubWeeklySummaryService : IWeeklyWorkSummaryService
    {
        public bool IsEnabled { get; set; } = true;
        public bool Allowed { get; set; }
        public int Calls { get; private set; }
        public DateOnly Start { get; private set; }
        public DateOnly End { get; private set; }
        public Exception? Error { get; set; }
        public Task<bool> CanGenerateAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsEnabled && Allowed);
        public Task<WeeklyWorkSummaryPreview> GenerateAsync(DateOnly start, DateOnly end, ClaimsPrincipal user,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Start = start;
            End = end;
            if (Error is not null) throw Error;
            return Task.FromResult(new WeeklyWorkSummaryPreview(new WeeklyWorkSummaryDraft(
                [new WeeklyWorkSummaryRow("Studio", "Support activity")], "Test", "Test"), []));
        }
    }

    private sealed class StubWeeklyWorkflow : IWeeklyWorkSummaryWorkflow
    {
        public WeeklyWorkSummaryRequest? Request { get; private set; }
        public Task<WeeklyWorkSummaryDraft> GenerateAsync(WeeklyWorkSummaryRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new WeeklyWorkSummaryDraft([], "Test", "Test"));
        }
    }

    private sealed class StubCurrentAccess(AccessLevel reports, AccessLevel studio = AccessLevel.ReadOnly,
        string userType = TeamHubUserTypes.Admin) : ICurrentAccessService
    {
        public Task<CurrentAccessSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CurrentAccessSnapshot(userType, null));
        public Task<AccessLevel> GetAccessLevelAsync(string module, CancellationToken cancellationToken = default) =>
            Task.FromResult(userType == TeamHubUserTypes.Admin
                ? AccessLevel.FullAccess
                : module == TeamHubModules.AiReports ? reports : studio);
        public async Task<bool> CanAsync(string module, AccessLevel required = AccessLevel.ReadOnly,
            CancellationToken cancellationToken = default) =>
            (await GetAccessLevelAsync(module, cancellationToken)).Allows(required);
    }

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
        public UpdateStudioConfluenceCommand? LastUpdateCommand { get; private set; }
        public StudioConfluenceUpdateResult? Result { get; set; }

        public Task<StudioConfluenceUpdateResult> GetUpdatesAsync(
            StudioConfluenceUpdateQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StudioConfluenceUpdateResult> GetConsolidatedUpdatesAsync(
            ConsolidatedStudioConfluenceUpdateQuery query,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(Result ?? new StudioConfluenceUpdateResult
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

        public Task<UpdateStudioConfluenceResult> UpdateAsync(
            UpdateStudioConfluenceCommand command,
            CancellationToken cancellationToken = default)
        {
            LastUpdateCommand = command;
            return Task.FromResult(new UpdateStudioConfluenceResult(true, "Saved."));
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
