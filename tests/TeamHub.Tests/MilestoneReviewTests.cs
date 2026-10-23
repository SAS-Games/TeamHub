using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using FluentAssertions;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TeamHub.Authentication;
using TeamHub.Milestones;
using TeamHub.MilestoneReview;
using TeamHub.Studio;
using TeamHub.Web.AccessControl;
using TeamHub.Web.Pages;
using TeamHub.Web.ExternalProcesses;
using TeamHub.Web.ExternalProcesses.MilestoneReviewReport;
using TeamHub.Web.ExternalProcesses.WorklogAnalytics;

namespace TeamHub.Tests;

public sealed class MilestoneReviewTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private MilestoneReviewDbContext dbContext = null!;
    private MilestoneReviewService service = null!;
    private readonly RecordingCommentPublisher commentPublisher = new();
    private readonly Guid reviewerOne = Guid.NewGuid();
    private readonly Guid reviewerTwo = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MilestoneReviewDbContext>()
            .UseSqlite(connection)
            .Options;
        dbContext = new MilestoneReviewDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        service = new MilestoneReviewService(dbContext, new StubUserDirectory(
        [
            new MilestoneReviewUser(reviewerOne, "Reviewer One", "reviewer.one@example.com", true, true),
            new MilestoneReviewUser(reviewerTwo, "Reviewer Two", "reviewer.two@example.com", true, true),
            new MilestoneReviewUser(Guid.NewGuid(), "Inactive", "inactive@example.com", false, true),
            new MilestoneReviewUser(Guid.NewGuid(), "Not registered", "unknown@example.com", true, false)
        ]), commentPublisher);
    }

    public sealed class MilestoneReviewDatabaseMigrationTests
    {
        [Fact]
        public async Task InitializerAddsJiraAndReviewerColumnsToExistingDatabase()
        {
            var path = Path.Combine(Path.GetTempPath(), $"milestone-review-migration-{Guid.NewGuid():N}.db");
            try
            {
                await using (var connection = new SqliteConnection($"Data Source={path}"))
                {
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        CREATE TABLE MilestoneReviews (
                            Id TEXT, ProjectName TEXT, MilestoneName TEXT, Description TEXT,
                            Status INTEGER, CreatedBy TEXT, CreatedAt TEXT, PublishedAt TEXT);
                        CREATE UNIQUE INDEX IX_MilestoneReviews_Status
                            ON MilestoneReviews (Status) WHERE Status IN (0, 1);
                        CREATE TABLE MilestoneReviewAssignments (
                            ReviewId TEXT, ReviewerId TEXT, Reviewer TEXT);
                        CREATE TABLE MilestoneReviewFeedback (
                            Id TEXT, ReviewId TEXT, ReviewerId TEXT, Reviewer TEXT,
                            WhatGood TEXT, WhatsBad TEXT, NeedImprovements TEXT,
                            Version INTEGER, UpdatedAt TEXT);
                        """;
                    await command.ExecuteNonQueryAsync();
                }

                var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ConnectionStrings:MilestoneReviewDb"] = $"Data Source={path}" })
                    .Build();
                await using var provider = new ServiceCollection()
                    .AddMilestoneReview(configuration)
                    .BuildServiceProvider();
                await using var scope = provider.CreateAsyncScope();
                var initializer = scope.ServiceProvider.GetRequiredService<IMilestoneReviewDatabaseInitializer>();
                await initializer.InitializeAsync();
                await initializer.InitializeAsync();
                var context = scope.ServiceProvider.GetRequiredService<MilestoneReviewDbContext>();

                await using var columns = context.Database.GetDbConnection().CreateCommand();
                columns.CommandText = """
                    SELECT name FROM pragma_table_info('MilestoneReviews')
                    UNION ALL SELECT name FROM pragma_table_info('MilestoneReviewAssignments')
                    UNION ALL SELECT name FROM pragma_table_info('MilestoneReviewFeedback')
                    """;
                await context.Database.OpenConnectionAsync();
                await using var reader = await columns.ExecuteReaderAsync();
                var addedColumns = new List<string>();
                while (await reader.ReadAsync()) addedColumns.Add(reader.GetString(0));
                addedColumns.Should().Contain("JiraTicketId", "WorkflowKey", "WorkflowInstanceId",
                    "ReviewerUserId", "JiraCommentId", "JiraSyncError");
                addedColumns.Should().Contain("SessionDetails", "AdditionalFeedback");
                addedColumns.Should().NotContain("WhatsBad");
                await reader.DisposeAsync();

                await using var index = context.Database.GetDbConnection().CreateCommand();
                index.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'IX_MilestoneReviews_Status'";
                (await index.ExecuteScalarAsync()).Should().BeOfType<string>()
                    .Which.Should().Contain("WHERE \"Status\" = 0").And.NotContain("IN (0, 1)");

                await using var reportGenerators = context.Database.GetDbConnection().CreateCommand();
                reportGenerators.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'MilestoneReviewReportGenerators'";
                Convert.ToInt32(await reportGenerators.ExecuteScalarAsync()).Should().Be(1);

                await using var insert = context.Database.GetDbConnection().CreateCommand();
                insert.CommandText = "INSERT INTO MilestoneReviews (Id, Status) VALUES ('one', 1), ('two', 1)";
                (await insert.ExecuteNonQueryAsync()).Should().Be(2);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                File.Delete(path);
            }
        }
    }

    public async Task DisposeAsync()
    {
        await dbContext.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task SchemaUpdateRetainsSupportedFeedbackAndAllowsNewRowsAfterUpgrade()
    {
        var path = Path.Combine(Path.GetTempPath(), $"milestone-review-schema-{Guid.NewGuid():N}.db");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:MilestoneReviewDb"] = $"Data Source={path}" })
                .Build();
            await using var provider = new ServiceCollection().AddMilestoneReview(configuration).BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var initializer = scope.ServiceProvider.GetRequiredService<IMilestoneReviewDatabaseInitializer>();
            await initializer.InitializeAsync();
            var context = scope.ServiceProvider.GetRequiredService<MilestoneReviewDbContext>();
            var upgradedService = new MilestoneReviewService(context, new StubUserDirectory(
                [new MilestoneReviewUser(reviewerOne, "Reviewer One", "reviewer.one@example.com", true, true)]),
                commentPublisher);
            await upgradedService.CreateDraftAsync(
                new CreateMilestoneReviewRequest("Project", "Milestone", "", null, [reviewerOne]), "admin@example.com");
            await upgradedService.PublishDraftAsync("admin@example.com");
            var current = (await upgradedService.GetCurrentAsync())!;
            var row = current.Feedback.Single();
            await upgradedService.SaveFeedbackAsync(
                new SaveMilestoneReviewFeedbackRequest(current.Id, row.Id, row.Version, "", "Good", "Improve", ""),
                reviewerOne, isAdmin: false);

            await context.Database.ExecuteSqlRawAsync("""
                ALTER TABLE MilestoneReviewFeedback DROP COLUMN SessionDetails;
                ALTER TABLE MilestoneReviewFeedback DROP COLUMN AdditionalFeedback;
                ALTER TABLE MilestoneReviewFeedback ADD COLUMN WhatsBad TEXT NOT NULL DEFAULT '';
                UPDATE MilestoneReviewFeedback SET WhatsBad = 'Obsolete development feedback';
                """);
            context.ChangeTracker.Clear();
            await initializer.InitializeAsync();
            await initializer.InitializeAsync();

            var upgraded = (await upgradedService.GetCurrentAsync())!.Feedback.Single();
            upgraded.WhatGood.Should().Be("Good");
            upgraded.NeedImprovements.Should().Be("Improve");
            upgraded.SessionDetails.Should().BeEmpty();
            upgraded.AdditionalFeedback.Should().BeEmpty();
            upgraded.Id.Should().Be(row.Id);
            await upgradedService.SaveFeedbackAsync(
                new SaveMilestoneReviewFeedbackRequest(current.Id, upgraded.Id, upgraded.Version, "PC", "Good", "Improve", "Art"),
                reviewerOne, isAdmin: false);
            (await upgradedService.GetCurrentAsync())!.Feedback.Single().AdditionalFeedback.Should().Be("Art");
            await upgradedService.CreateDraftAsync(
                new CreateMilestoneReviewRequest("Project", "Next", "", null, [reviewerOne]), "admin@example.com");
            await upgradedService.PublishDraftAsync("admin@example.com");
            (await upgradedService.GetCurrentAsync())!.Feedback.Should().ContainSingle();
            (await upgradedService.GetHistoryAsync()).Should().BeEmpty();
            var active = await upgradedService.GetActiveAsync();
            active.Should().HaveCount(2);
            active.Single(item => item.Id == current.Id).Feedback.Single().SessionDetails.Should().Be("PC");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PublishCreatesSharedRowsAndOnlyAssignedReviewerCanEditOwnRow()
    {
        await CreateAndPublishAsync([reviewerOne, reviewerTwo]);
        var current = await service.GetCurrentAsync();

        current.Should().NotBeNull();
        current!.Feedback.Should().HaveCount(2);
        current.Feedback.Select(row => row.Reviewer).Should().BeEquivalentTo("Reviewer One", "Reviewer Two");

        var row = current.Feedback.Single(item => item.ReviewerId == reviewerOne);
        var save = new SaveMilestoneReviewFeedbackRequest(
            current.Id, row.Id, row.Version, "PC, 1 hour, levels 1-3", "Good", "Improve", "Art notes");
        await service.SaveFeedbackAsync(save, reviewerOne, isAdmin: false);
        var updated = (await service.GetCurrentAsync())!.Feedback.Single(item => item.Id == row.Id);
        updated.WhatGood.Should().Be("Good");
        updated.SessionDetails.Should().Be("PC, 1 hour, levels 1-3");
        updated.NeedImprovements.Should().Be("Improve");
        updated.AdditionalFeedback.Should().Be("Art notes");
        updated.Version.Should().Be(row.Version + 1);
        await using (var reloadedContext = new MilestoneReviewDbContext(
            new DbContextOptionsBuilder<MilestoneReviewDbContext>().UseSqlite(connection).Options))
        {
            var reloaded = await new MilestoneReviewService(
                reloadedContext, new StubUserDirectory([]), commentPublisher).GetCurrentAsync();
            reloaded!.Feedback.Single(item => item.Id == row.Id).WhatGood.Should().Be("Good");
            reloaded.Feedback.Single(item => item.Id == row.Id).Should().BeEquivalentTo(updated);
        }

        var otherUser = () => service.SaveFeedbackAsync(
            save with { Version = updated.Version, WhatGood = "Overwrite" }, reviewerTwo, isAdmin: false);
        await otherUser.Should().ThrowAsync<UnauthorizedAccessException>();
        (await service.GetCurrentAsync())!.Feedback.Single(item => item.Id == row.Id).WhatGood.Should().Be("Good");
    }

    [Theory]
    [InlineData("SessionDetails")]
    [InlineData("WhatGood")]
    [InlineData("NeedImprovements")]
    [InlineData("AdditionalFeedback")]
    public async Task FeedbackFieldsAcceptLimitAndRejectOverLimitWithoutPartialChanges(string field)
    {
        await CreateAndPublishAsync([reviewerOne]);
        var current = (await service.GetCurrentAsync())!;
        var row = current.Feedback.Single();
        var baseline = new SaveMilestoneReviewFeedbackRequest(current.Id, row.Id, row.Version, "", "", "", "");
        SaveMilestoneReviewFeedbackRequest WithText(string text) => field switch
        {
            "SessionDetails" => baseline with { SessionDetails = text },
            "WhatGood" => baseline with { WhatGood = text },
            "NeedImprovements" => baseline with { NeedImprovements = text },
            "AdditionalFeedback" => baseline with { AdditionalFeedback = text },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        await service.SaveFeedbackAsync(WithText(new string('a', 20000)), reviewerOne, isAdmin: false);
        var saved = (await service.GetCurrentAsync())!.Feedback.Single();
        baseline = baseline with { Version = saved.Version };

        var invalidSave = () => service.SaveFeedbackAsync(
            WithText(new string('b', 20001)), reviewerOne, isAdmin: false);
        await invalidSave.Should().ThrowAsync<ArgumentException>().WithMessage("*20000*");
        (await service.GetCurrentAsync())!.Feedback.Single().Should().BeEquivalentTo(saved);
        dbContext.ChangeTracker.HasChanges().Should().BeFalse();
    }

    [Fact]
    public async Task AdminCanEditAnyAssignedRowAndStaleVersionsAreRejected()
    {
        await CreateAndPublishAsync([reviewerOne]);
        var current = (await service.GetCurrentAsync())!;
        var row = current.Feedback.Single();
        var request = new SaveMilestoneReviewFeedbackRequest(
            current.Id, row.Id, row.Version, "", "First save", "", "");
        await service.SaveFeedbackAsync(request, reviewerOne, isAdmin: false);

        var stale = () => service.SaveFeedbackAsync(request, reviewerOne, isAdmin: false);
        await stale.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*changed by another user*");

        await service.SaveFeedbackAsync(
            request with { Version = row.Version + 1, WhatGood = "Admin correction" },
            reviewerTwo, isAdmin: true);
        (await service.GetCurrentAsync())!.Feedback.Single().WhatGood.Should().Be("Admin correction");
    }

    [Fact]
    public async Task PublishingNewReviewKeepsPreviousReviewActive()
    {
        await CreateAndPublishAsync([reviewerOne], "First milestone");
        var old = (await service.GetCurrentAsync())!;
        await service.CreateDraftAsync(
            new CreateMilestoneReviewRequest("Project", "Second milestone", "", null, [reviewerTwo]),
            "admin@example.com");
        await service.PublishDraftAsync("admin@example.com");

        var current = (await service.GetCurrentAsync())!;
        current.MilestoneName.Should().Be("Second milestone");
        current.Feedback.Select(item => item.ReviewerId).Should().ContainSingle().Which.Should().Be(reviewerTwo);

        var active = await service.GetActiveAsync();
        active.Select(item => item.MilestoneName).Should().Equal("Second milestone", "First milestone");
        active.Should().OnlyContain(item => item.Status == MilestoneReviewStatus.Published);
        var oldFeedback = old.Feedback.Single();
        await service.SaveFeedbackAsync(
            new SaveMilestoneReviewFeedbackRequest(old.Id, oldFeedback.Id, oldFeedback.Version, "", "Late", "", ""),
            reviewerOne, isAdmin: true);

        (await service.GetActiveAsync()).Single(item => item.Id == old.Id)
            .Feedback.Single().WhatGood.Should().Be("Late");
        (await service.GetHistoryAsync()).Should().BeEmpty();

        var model = CreateModel(service, allowed: true);
        model.SelectedReviewId = old.Id;
        (await model.OnGetAsync(CancellationToken.None)).Should().BeOfType<PageResult>();
        model.ActiveReviews.Should().HaveCount(2);
        model.CurrentReview!.Id.Should().Be(old.Id);

        await service.ArchiveAsync(old.Id, isAdmin: true);
        (await service.GetActiveAsync()).Should().ContainSingle().Which.Id.Should().Be(current.Id);
        (await service.GetHistoryAsync()).Should().ContainSingle().Which.Id.Should().Be(old.Id);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsSupersededReviewsOrderedByPublishedAtDescending()
    {
        (await service.GetHistoryAsync()).Should().BeEmpty();

        await CreateAndPublishAsync([reviewerOne], "Milestone 1");
        (await service.GetHistoryAsync()).Should().BeEmpty();

        var current = (await service.GetCurrentAsync())!;
        var fb = current.Feedback.Single();
        await service.SaveFeedbackAsync(
            new SaveMilestoneReviewFeedbackRequest(current.Id, fb.Id, fb.Version, "PC", "M1 Good", "M1 Improve", "M1 Art"),
            reviewerOne, isAdmin: false);

        await service.CreateDraftAsync(
            new CreateMilestoneReviewRequest("Project", "Milestone 2", "M2 Desc", "TICK-2", [reviewerTwo]),
            "admin@example.com");
        await service.PublishDraftAsync("admin@example.com");
        await service.ArchiveAsync(current.Id, isAdmin: true);

        var historyAfterSecond = await service.GetHistoryAsync();
        historyAfterSecond.Should().HaveCount(1);
        historyAfterSecond[0].MilestoneName.Should().Be("Milestone 1");
        historyAfterSecond[0].Feedback.Single().WhatGood.Should().Be("M1 Good");

        await service.CreateDraftAsync(
            new CreateMilestoneReviewRequest("Project", "Milestone 3", "M3 Desc", "TICK-3", [reviewerOne]),
            "admin@example.com");
        await service.PublishDraftAsync("admin@example.com");
        var milestoneTwo = (await service.GetActiveAsync()).Single(item => item.MilestoneName == "Milestone 2");
        await service.ArchiveAsync(milestoneTwo.Id, isAdmin: true);

        var historyAfterThird = await service.GetHistoryAsync();
        historyAfterThird.Should().HaveCount(2);
        historyAfterThird.Select(item => item.MilestoneName).Should().Equal("Milestone 2", "Milestone 1");
        historyAfterThird[0].JiraTicketId.Should().Be("TICK-2");
    }

    [Fact]
    public async Task CreateDraftRejectsUsersWhoCannotSignInAndDuplicateReviewers()
    {
        var inactive = Guid.NewGuid();
        var serviceWithInactive = new MilestoneReviewService(dbContext, new StubUserDirectory(
            [new MilestoneReviewUser(inactive, "Inactive", "inactive@example.com", false, true)]),
            commentPublisher);
        var invalidUser = () => serviceWithInactive.CreateDraftAsync(
            new CreateMilestoneReviewRequest("Project", "Milestone", "", null, [inactive]), "admin@example.com");
        await invalidUser.Should().ThrowAsync<ArgumentException>();

        var duplicate = () => service.CreateDraftAsync(
            new CreateMilestoneReviewRequest("Project", "Milestone", "", null, [reviewerOne, reviewerOne]), "admin@example.com");
        await duplicate.Should().ThrowAsync<ArgumentException>();

        var invalidTicket = () => service.CreateDraftAsync(
            new CreateMilestoneReviewRequest("Project", "Milestone", "", "GAME/123", [reviewerOne]),
            "admin@example.com");
        await invalidTicket.Should().ThrowAsync<ArgumentException>().WithMessage("*project-key format*");
    }

    [Fact]
    public async Task ArchivePreservesFeedbackAndDraftAndClosesEditing()
    {
        await CreateAndPublishAsync([reviewerOne]);
        var current = (await service.GetCurrentAsync())!;
        var row = current.Feedback.Single();
        var save = new SaveMilestoneReviewFeedbackRequest(current.Id, row.Id, row.Version, "PC", "Good", "Improve", "Art");
        await service.SaveFeedbackAsync(save, reviewerOne, isAdmin: false);
        var saved = (await service.GetCurrentAsync())!;
        var draft = await service.CreateDraftAsync(
            new CreateMilestoneReviewRequest("Project", "Next milestone", "", null, [reviewerTwo]),
            "admin@example.com");

        await service.ArchiveAsync(current.Id, isAdmin: true);

        (await service.GetCurrentAsync()).Should().BeNull();
        (await service.GetDraftAsync())!.Id.Should().Be(draft!.Id);
        var archived = (await service.GetHistoryAsync()).Should().ContainSingle().Subject;
        archived.Status.Should().Be(MilestoneReviewStatus.Superseded);
        archived.Feedback.Should().BeEquivalentTo(saved.Feedback);
        archived.AssignedReviewers.Should().BeEquivalentTo(saved.AssignedReviewers);
        var lateSave = () => service.SaveFeedbackAsync(
            save with { Version = saved.Feedback.Single().Version }, reviewerOne, isAdmin: true);
        await lateSave.Should().ThrowAsync<InvalidOperationException>().WithMessage("*active published review*");

        await service.PublishDraftAsync("admin@example.com");
        (await service.GetCurrentAsync())!.Id.Should().Be(draft.Id);
        (await service.GetHistoryAsync()).Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteRemovesPublishedOrArchivedReviewAndOnlyItsDependentRows(bool archiveFirst)
    {
        await service.CreateDraftAsync(
            new CreateMilestoneReviewRequest("Project", "First", "", "GAME-123", [reviewerOne]),
            "admin@example.com");
        await service.PublishDraftAsync("admin@example.com");
        var target = (await service.GetCurrentAsync())!;
        var row = target.Feedback.Single();
        await service.SaveFeedbackAsync(
            new SaveMilestoneReviewFeedbackRequest(target.Id, row.Id, row.Version, "", "Good", "", ""),
            reviewerOne, isAdmin: false);
        await service.CreateDraftAsync(
            new CreateMilestoneReviewRequest("Project", "Second", "", null, [reviewerTwo]),
            "admin@example.com");
        if (archiveFirst)
            await service.PublishDraftAsync("admin@example.com");
        var otherDraft = await service.GetDraftAsync();
        var otherCurrent = await service.GetCurrentAsync();
        var jiraWrites = commentPublisher.Requests.Count;

        await service.DeleteAsync(target.Id, isAdmin: true);

        (await service.GetHistoryAsync()).Should().BeEmpty();
        var remainingCurrent = await service.GetCurrentAsync();
        var remainingDraft = await service.GetDraftAsync();
        remainingCurrent.Should().BeEquivalentTo(archiveFirst ? otherCurrent : null);
        remainingDraft.Should().BeEquivalentTo(otherDraft);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM MilestoneReviews WHERE Id = $id) +
                (SELECT COUNT(*) FROM MilestoneReviewAssignments WHERE ReviewId = $id) +
                (SELECT COUNT(*) FROM MilestoneReviewFeedback WHERE ReviewId = $id);
            """;
        command.Parameters.AddWithValue("$id", target.Id);
        Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(0);
        commentPublisher.Requests.Should().HaveCount(jiraWrites);
        var preview = await CreateModel(service, allowed: true).OnGetFeedbackAsync(target.Id, CancellationToken.None);
        preview.Should().BeOfType<JsonResult>().Which.StatusCode.Should().Be(404);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceRejectsNonAdminArchiveAndDelete(bool delete)
    {
        await CreateAndPublishAsync([reviewerOne]);
        var current = (await service.GetCurrentAsync())!;
        var action = () => delete
            ? service.DeleteAsync(current.Id, isAdmin: false)
            : service.ArchiveAsync(current.Id, isAdmin: false);

        await action.Should().ThrowAsync<UnauthorizedAccessException>();
        (await service.GetCurrentAsync())!.Id.Should().Be(current.Id);
    }

    [Theory]
    [InlineData(TeamHubUserTypes.Registered)]
    [InlineData(TeamHubUserTypes.Privileged)]
    public async Task HandlersRejectNonAdminEvenWithPageAccess(string role)
    {
        await CreateAndPublishAsync([reviewerOne]);
        var current = (await service.GetCurrentAsync())!;
        var model = CreateModel(service, allowed: true);
        model.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role)], "Test"));
        model.ReviewId = current.Id;

        (await model.OnPostArchiveAsync(CancellationToken.None)).Should().BeOfType<ForbidResult>();
        (await model.OnPostDeleteAsync(CancellationToken.None)).Should().BeOfType<ForbidResult>();
        (await service.GetCurrentAsync())!.Id.Should().Be(current.Id);
    }

    [Fact]
    public async Task AdminHandlersArchiveAndDeleteAndReportStaleRequests()
    {
        await CreateAndPublishAsync([reviewerOne]);
        var model = CreateModel(service, allowed: true);
        model.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, TeamHubUserTypes.Admin)], "Test"));
        model.ReviewId = (await service.GetCurrentAsync())!.Id;

        (await model.OnPostArchiveAsync(CancellationToken.None)).Should().BeOfType<RedirectToPageResult>();
        model.StatusMessage.Should().Contain("archived");
        (await service.GetCurrentAsync()).Should().BeNull();
        (await model.OnPostArchiveAsync(CancellationToken.None)).Should().BeOfType<RedirectToPageResult>();
        model.ErrorMessage.Should().Contain("already been archived");
        (await model.OnPostDeleteAsync(CancellationToken.None)).Should().BeOfType<RedirectToPageResult>();
        model.StatusMessage.Should().Contain("permanently deleted");
        (await service.GetHistoryAsync()).Should().BeEmpty();
        (await model.OnPostDeleteAsync(CancellationToken.None)).Should().BeOfType<RedirectToPageResult>();
        model.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task ArchiveAndDeleteRejectDraftsAndUnknownIds()
    {
        var draft = (await service.CreateDraftAsync(
            new CreateMilestoneReviewRequest("Project", "Draft", "", null, [reviewerOne]),
            "admin@example.com"))!;
        foreach (var id in new[] { draft.Id, Guid.Empty, Guid.NewGuid() })
        {
            var archive = () => service.ArchiveAsync(id, isAdmin: true);
            var delete = () => service.DeleteAsync(id, isAdmin: true);
            await archive.Should().ThrowAsync<InvalidOperationException>();
            await delete.Should().ThrowAsync<InvalidOperationException>();
        }
        (await service.GetDraftAsync())!.Id.Should().Be(draft.Id);
    }

    private async Task CreateAndPublishAsync(IReadOnlyCollection<Guid> reviewers, string milestone = "Milestone")
    {
        await service.CreateDraftAsync(
            new CreateMilestoneReviewRequest("Project", milestone, "Review description", null, reviewers),
            "admin@example.com");
        (await service.GetCurrentAsync()).Should().BeNull();
        (await service.GetDraftAsync()).Should().NotBeNull();
        await service.PublishDraftAsync("admin@example.com");
    }

    [Fact]
    public async Task PublishingDraftStartsAssignedWorkflowAndStoresInstanceId()
    {
        var workflowStarter = new RecordingWorkflowStarter();
        var workflowService = new MilestoneReviewService(
            dbContext,
            new StubUserDirectory([
                new MilestoneReviewUser(reviewerOne, "Reviewer One", "reviewer.one@example.com", true, true)
            ]),
            commentPublisher,
            workflowStarter);
        await workflowService.CreateDraftAsync(
            new CreateMilestoneReviewRequest(
                "Project One", "Alpha", "Review build", null, [reviewerOne], "MILESTONE_REVIEW"),
            "admin@example.com");

        var published = await workflowService.PublishDraftAsync("publisher@example.com");

        published!.WorkflowKey.Should().Be("MILESTONE_REVIEW");
        published.WorkflowInstanceId.Should().Be(workflowStarter.InstanceId);
        workflowStarter.Requests.Should().ContainSingle();
        workflowStarter.Requests[0].InstanceName.Should().Be("Project One Review build Alpha");
        workflowStarter.Requests[0].StartedBy.Should().Be("publisher@example.com");
        workflowStarter.Requests[0].MetadataJson.Should().Contain(published.Id.ToString());
        using var metadata = JsonDocument.Parse(workflowStarter.Requests[0].MetadataJson);
        metadata.RootElement.GetProperty("MilestoneName").GetString().Should().Be("Alpha Review build");
        metadata.RootElement.GetProperty("Description").GetString().Should().Be("Review build");
    }

    private sealed class StubUserDirectory(IReadOnlyList<MilestoneReviewUser> users) : IMilestoneReviewUserDirectory
    {
        public Task<IReadOnlyList<MilestoneReviewUser>> GetActiveUsersAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(users);
    }

    private sealed class RecordingWorkflowStarter : IMilestoneReviewWorkflowStarter
    {
        public Guid InstanceId { get; } = Guid.NewGuid();
        public List<MilestoneReviewWorkflowStartRequest> Requests { get; } = [];

        public Task<Guid> StartAsync(
            MilestoneReviewWorkflowStartRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(InstanceId);
        }
    }

    [Fact]
    public async Task JiraTicketSaveCreatesThenUpdatesReviewersOwnComment()
    {
        await service.CreateDraftAsync(
            new CreateMilestoneReviewRequest("Project", "Milestone", "Description", "GAME-123", [reviewerOne]),
            "admin@example.com");
        await service.PublishDraftAsync("admin@example.com");
        var current = (await service.GetCurrentAsync())!;
        var row = current.Feedback.Single();

        var firstSave = new SaveMilestoneReviewFeedbackRequest(
            current.Id, row.Id, row.Version, "PC, 2 hours", "Good", "Improve", "Audio notes");
        await service.SaveFeedbackAsync(firstSave, reviewerOne, isAdmin: false);
        var saved = (await service.GetCurrentAsync())!.Feedback.Single();
        saved.JiraCommentId.Should().Be("42");
        commentPublisher.Requests.Should().ContainSingle();
        commentPublisher.Requests[0].RequestingUserId.Should().Be("reviewer.one@example.com");
        commentPublisher.Requests[0].TicketId.Should().Be("GAME-123");
        commentPublisher.Requests[0].SessionDetails.Should().Be("PC, 2 hours");
        commentPublisher.Requests[0].WhatGood.Should().Be("Good");
        commentPublisher.Requests[0].NeedImprovements.Should().Be("Improve");
        commentPublisher.Requests[0].AdditionalFeedback.Should().Be("Audio notes");

        await service.SaveFeedbackAsync(
            firstSave with { Version = saved.Version, WhatGood = "Updated good",
                SessionDetails = "Console, 3 hours", AdditionalFeedback = "Updated audio notes" },
            reviewerOne, isAdmin: false);
        commentPublisher.Requests.Should().HaveCount(2);
        commentPublisher.Requests[1].CommentId.Should().Be("42");
        commentPublisher.Requests[1].WhatGood.Should().Be("Updated good");
        commentPublisher.Requests[1].SessionDetails.Should().Be("Console, 3 hours");
        commentPublisher.Requests[1].AdditionalFeedback.Should().Be("Updated audio notes");
    }

    [Fact]
    public async Task JiraFailureKeepsLocalFeedbackAndShowsRetryableSyncError()
    {
        await service.CreateDraftAsync(
            new CreateMilestoneReviewRequest("Project", "Milestone", "", "GAME-123", [reviewerOne]),
            "admin@example.com");
        await service.PublishDraftAsync("admin@example.com");
        var current = (await service.GetCurrentAsync())!;
        var row = current.Feedback.Single();
        commentPublisher.Error = new MilestoneReviewCommentSyncException("Jira comment update failed.");
        var save = () => service.SaveFeedbackAsync(
            new SaveMilestoneReviewFeedbackRequest(current.Id, row.Id, row.Version, "", "Saved locally", "", ""),
            reviewerOne, isAdmin: false);

        await save.Should().ThrowAsync<MilestoneReviewCommentSyncException>()
            .WithMessage("Jira comment update failed.");
        var saved = (await service.GetCurrentAsync())!.Feedback.Single();
        saved.WhatGood.Should().Be("Saved locally");
        saved.JiraSyncError.Should().Be("Jira comment update failed.");
    }

    private sealed class RecordingCommentPublisher : IMilestoneReviewCommentPublisher
    {
        public List<MilestoneReviewCommentRequest> Requests { get; } = [];
        public MilestoneReviewCommentSyncException? Error { get; set; }

        public Task<MilestoneReviewCommentResult> UpsertAsync(
            MilestoneReviewCommentRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Error is not null) throw Error;
            return Task.FromResult(new MilestoneReviewCommentResult(request.CommentId ?? "42"));
        }
    }

    [Fact]
    public async Task AdminCanEditDraftDetailsAndPublishUpdatedContributors()
    {
        var draft = (await service.CreateDraftAsync(
            new("Project", "Milestone", "", null, [reviewerOne]), "admin@example.com"))!;
        await service.UpdateAsync(draft.Id,
            new(" Updated Project ", " Updated Milestone ", "**Updated description**", "GAME-123", [reviewerTwo]), true);
        dbContext.ChangeTracker.Clear();
        var saved = (await service.GetDraftAsync())!;
        saved.ProjectName.Should().Be("Updated Project");
        saved.MilestoneName.Should().Be("Updated Milestone");
        saved.Description.Should().Be("**Updated description**");
        saved.JiraTicketId.Should().Be("GAME-123");
        saved.CreatedAt.Should().Be(draft.CreatedAt);
        saved.Status.Should().Be(MilestoneReviewStatus.Draft);
        saved.Feedback.Should().BeEmpty();
        saved.AssignedReviewers.Should().ContainSingle().Which.Id.Should().Be(reviewerTwo);
        await service.PublishDraftAsync("admin@example.com");
        (await service.GetCurrentAsync())!.Feedback.Should().ContainSingle().Which.ReviewerId.Should().Be(reviewerTwo);
    }

    [Fact]
    public async Task ReportGeneratorsAreStoredAndUpdatedIndependentlyFromFeedbackContributors()
    {
        var draft = (await service.CreateDraftAsync(
            new("Project", "Milestone", "", null, [reviewerOne], ReportGeneratorIds: [reviewerTwo]),
            "admin@example.com"))!;

        draft.AssignedReviewers.Should().ContainSingle().Which.Id.Should().Be(reviewerOne);
        draft.ReportGenerators.Should().ContainSingle().Which.Id.Should().Be(reviewerTwo);

        await service.UpdateAsync(draft.Id,
            new("Project", "Milestone", "", null, [reviewerTwo], ReportGeneratorIds: [reviewerOne]), true);
        var updated = (await service.GetDraftAsync())!;

        updated.AssignedReviewers.Should().ContainSingle().Which.Id.Should().Be(reviewerTwo);
        updated.ReportGenerators.Should().ContainSingle().Which.Id.Should().Be(reviewerOne);
    }

    [Fact]
    public async Task GenerateReportVisibilityRequiresExplicitReportGeneratorAssignment()
    {
        await service.CreateDraftAsync(
            new("Project", "Milestone", "", null, [reviewerOne], ReportGeneratorIds: [reviewerTwo]),
            "admin@example.com");
        await service.PublishDraftAsync("admin@example.com");

        var feedbackContributor = CreateModel(service, true, user: ReviewUser());
        await feedbackContributor.OnGetAsync(CancellationToken.None);
        feedbackContributor.CanGenerateReport.Should().BeFalse();

        var assignedReportGenerator = CreateModel(service, true, user: new AuthorizedUserRecord(
            reviewerTwo, "reviewer.two@example.com", null, "Reviewer Two", TeamHubUserTypes.Registered,
            true, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        await assignedReportGenerator.OnGetAsync(CancellationToken.None);
        assignedReportGenerator.CanGenerateReport.Should().BeTrue();

        SetRole(feedbackContributor, TeamHubUserTypes.Admin);
        feedbackContributor.CanGenerateReport.Should().BeFalse(
            "administrators must also be explicitly assigned as report generators");
    }

    [Fact]
    public async Task MilestoneReportProcessSendsVersionedServerLoadedSnapshotAndRechecksAssignment()
    {
        await service.CreateDraftAsync(
            new("Project", "Milestone", "Description", "GAME-123", [reviewerOne],
                ReportGeneratorIds: [reviewerTwo]),
            "admin@example.com");
        await service.PublishDraftAsync("admin@example.com");
        var current = (await service.GetCurrentAsync())!;
        var runner = new StubExternalProcessRunner
        {
            Result = new("""
                {"success":true,"reportId":"report-1","reportUrl":"https://reports.example.test/1","message":"Ready"}
                """, "", 0)
        };
        var credentialResolver = new StubExternalProcessCredentialResolver();
        var reportProcess = new MilestoneReviewReportProcessService(service, runner, credentialResolver);

        var result = await reportProcess.GenerateAsync(
            current.Id, reviewerTwo, "reviewer.two@example.com", false);

        result.ReportId.Should().Be("report-1");
        runner.Requests.Should().ContainSingle();
        credentialResolver.ProcessKey.Should().Be(ExternalProcessKeys.MilestoneReviewReport);
        using var input = System.Text.Json.JsonDocument.Parse(runner.Requests.Single().StandardInput!);
        input.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        input.RootElement.GetProperty("correlationId").GetGuid().Should().NotBeEmpty();
        var review = input.RootElement.GetProperty("milestoneReview");
        review.GetProperty("id").GetGuid().Should().Be(current.Id);
        review.GetProperty("jiraTicketId").GetString().Should().Be("GAME-123");
        review.GetProperty("feedback").GetArrayLength().Should().Be(1);

        var unauthorized = () => reportProcess.GenerateAsync(
            current.Id, reviewerOne, "reviewer.one@example.com", false);
        await unauthorized.Should().ThrowAsync<UnauthorizedAccessException>();
        runner.Requests.Should().ContainSingle("an unassigned user must not launch the external process");
    }

    [Fact]
    public async Task WorklogAnalyticsUsesSharedExternalProcessRunnerAndCredentialPolicy()
    {
        var runner = new StubExternalProcessRunner
        {
            Result = new("""
                {
                  "effortSummary":[{"label":"Studio","hours":12.5}],
                  "studioBreakdown":[],
                  "employeeEffortBreakdowns":[]
                }
                """, "", 0)
        };
        var credentials = new StubExternalProcessCredentialResolver();
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var worklogs = new PythonWorklogEffortService(
            runner, credentials, cache);

        var report = await worklogs.GetActualEffortAsync(
            new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 23),
            "privileged@example.com", true);

        report.EffortSummary.Should().ContainSingle().Which.Hours.Should().Be(12.5);
        var request = runner.Requests.Should().ContainSingle().Which;
        request.ProcessKey.Should().Be(ExternalProcessKeys.WorklogAnalytics);
        request.Arguments.Should().Equal("--start", "2026-10-19", "--end", "2026-10-23");
        request.SensitiveEnvironment.Should().ContainKey("TEAMHUB_JIRA_PAT");
        credentials.RequestingUserId.Should().Be("privileged@example.com");
        credentials.AllowPrivilegedDefaultCredential.Should().BeTrue();
    }

    [Fact]
    public async Task ExternalProcessCredentialPolicyCanUseConfiguredUsersBearerToken()
    {
        var services = new ServiceCollection();
        services.Configure<ExternalProcessCatalogOptions>(options => options.Processes[
            ExternalProcessKeys.MilestoneReviewReport] = new ExternalProcessOptions
        {
            Credential = new ExternalProcessCredentialOptions
            {
                Mode = ExternalProcessCredentialModes.ConfiguredUser,
                ConfiguredUserId = "report.admin@example.com",
                EnvironmentVariable = "MILESTONE_JIRA_PAT"
            }
        });
        var credentials = new StubAtlassianCredentials();
        services.AddSingleton<IAtlassianCredentialAccessor>(credentials);
        services.AddScoped<IExternalProcessCredentialResolver, ExternalProcessCredentialResolver>();
        await using var provider = services.BuildServiceProvider();

        var environment = await provider.GetRequiredService<IExternalProcessCredentialResolver>()
            .ResolveEnvironmentAsync(
                ExternalProcessKeys.MilestoneReviewReport, "requester@example.com", false);

        credentials.RequestingUserId.Should().Be("report.admin@example.com");
        credentials.AllowDefault.Should().BeFalse();
        environment.Should().Contain("MILESTONE_JIRA_PAT", "configured-admin-token");
    }

    [Fact]
    public async Task PublishedEditPreservesFeedbackAndRevokesRemovedContributorsWithoutDeletingTheirRows()
    {
        await CreateAndPublishAsync([reviewerOne]);
        var current = (await service.GetCurrentAsync())!;
        var row = current.Feedback.Single();
        await service.SaveFeedbackAsync(new(current.Id, row.Id, row.Version, "PC", "**Good**", "Improve", "Art"),
            reviewerOne, false);
        var savedRow = (await service.GetCurrentAsync())!.Feedback.Single();
        await service.UpdateAsync(current.Id, new("Updated", "M2", "New description", null, [reviewerTwo]), true);
        dbContext.ChangeTracker.Clear();
        var edited = (await service.GetCurrentAsync())!;
        edited.PublishedAt.Should().Be(current.PublishedAt);
        edited.CreatedAt.Should().Be(current.CreatedAt);
        edited.Feedback.Should().HaveCount(2);
        edited.Feedback.Single(item => item.ReviewerId == reviewerOne).Should().Be(savedRow);
        edited.AssignedReviewers.Should().ContainSingle().Which.Id.Should().Be(reviewerTwo);
        var unauthorized = () => service.SaveFeedbackAsync(
            new(current.Id, savedRow.Id, savedRow.Version, "", "Changed", "", ""), reviewerOne, false);
        await unauthorized.Should().ThrowAsync<UnauthorizedAccessException>();
        var added = edited.Feedback.Single(item => item.ReviewerId == reviewerTwo);
        await service.SaveFeedbackAsync(new(current.Id, added.Id, added.Version, "PS5", "", "", ""), reviewerTwo, false);
        await service.UpdateAsync(current.Id, new("Updated", "M2", "", null, [reviewerOne, reviewerTwo]), true);
        var restored = (await service.GetCurrentAsync())!;
        restored.Feedback.Should().HaveCount(2);
        restored.Feedback.Single(item => item.ReviewerId == reviewerOne).Should().Be(savedRow);
    }

    [Theory]
    [InlineData("GAME-456")]
    [InlineData(null)]
    public async Task ChangingJiraTicketPreservesFeedbackAndResetsCommentIdentityUntilNextSave(string? ticket)
    {
        await service.CreateDraftAsync(new("Project", "Milestone", "", "GAME-123", [reviewerOne]), "admin@example.com");
        await service.PublishDraftAsync("admin@example.com");
        var current = (await service.GetCurrentAsync())!;
        var row = current.Feedback.Single();
        await service.SaveFeedbackAsync(new(current.Id, row.Id, row.Version, "PC", "Good", "", ""), reviewerOne, false);
        var saved = (await service.GetCurrentAsync())!.Feedback.Single();
        saved.JiraCommentId.Should().Be("42");
        await service.UpdateAsync(current.Id, new("New project", "M2", "", ticket, [reviewerOne]), true);
        var edited = (await service.GetCurrentAsync())!.Feedback.Single();
        edited.Id.Should().Be(saved.Id);
        edited.WhatGood.Should().Be("Good");
        edited.JiraCommentId.Should().BeNull();
        edited.JiraSyncError.Should().BeNull();
        edited.Version.Should().Be(saved.Version + 1);
        commentPublisher.Requests.Should().ContainSingle();
        var stale = () => service.SaveFeedbackAsync(new(current.Id, edited.Id, saved.Version, "", "", "", ""),
            reviewerOne, false);
        await stale.Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed by another user*");
        await service.SaveFeedbackAsync(new(current.Id, edited.Id, edited.Version, "PC", "Good", "", ""), reviewerOne, false);
        if (ticket is null)
            commentPublisher.Requests.Should().ContainSingle();
        else
        {
            commentPublisher.Requests.Should().HaveCount(2);
            var request = commentPublisher.Requests.Last();
            request.TicketId.Should().Be(ticket);
            request.CommentId.Should().BeNull();
            request.ProjectName.Should().Be("New project");
            request.MilestoneName.Should().Be("M2");
        }
    }

    [Fact]
    public async Task EditRejectsNonAdminArchivedAndMissingReviews()
    {
        await CreateAndPublishAsync([reviewerOne]);
        var current = (await service.GetCurrentAsync())!;
        var details = new CreateMilestoneReviewRequest("Changed", "Changed", "", null, [reviewerTwo]);
        var denied = () => service.UpdateAsync(current.Id, details, false);
        await denied.Should().ThrowAsync<UnauthorizedAccessException>();
        await service.ArchiveAsync(current.Id, true);
        foreach (var id in new[] { current.Id, Guid.NewGuid(), Guid.Empty })
        {
            var invalid = () => service.UpdateAsync(id, details, true);
            await invalid.Should().ThrowAsync<InvalidOperationException>();
        }
        (await service.GetHistoryAsync()).Single().ProjectName.Should().Be(current.ProjectName);
    }

    [Fact]
    public async Task EditingDetailsWithSameTicketKeepsExistingCommentAndUpdatesItOnNextSave()
    {
        await service.CreateDraftAsync(new("Project", "Milestone", "", "GAME-123", [reviewerOne]), "admin@example.com");
        await service.PublishDraftAsync("admin@example.com");
        var current = (await service.GetCurrentAsync())!;
        var row = current.Feedback.Single();
        await service.SaveFeedbackAsync(new(current.Id, row.Id, row.Version, "", "Good", "", ""), reviewerOne, false);
        var saved = (await service.GetCurrentAsync())!.Feedback.Single();
        await service.UpdateAsync(current.Id, new("Edited", "M2", "Details", "GAME-123", [reviewerOne]), true);
        (await service.GetCurrentAsync())!.Feedback.Single().Should().Be(saved);
        commentPublisher.Requests.Should().ContainSingle();
        await service.SaveFeedbackAsync(new(current.Id, saved.Id, saved.Version, "", "Good", "", ""), reviewerOne, false);
        commentPublisher.Requests.Last().CommentId.Should().Be(saved.JiraCommentId);
        commentPublisher.Requests.Last().ProjectName.Should().Be("Edited");
        commentPublisher.Requests.Last().MilestoneName.Should().Be("M2");
    }

    [Fact]
    public async Task ExistingInactiveContributorCanBeRetainedButCannotBeNewlyAssigned()
    {
        await CreateAndPublishAsync([reviewerOne]);
        var current = (await service.GetCurrentAsync())!;
        var limitedDirectoryService = new MilestoneReviewService(dbContext,
            new StubUserDirectory([new(reviewerTwo, "Reviewer Two", "reviewer.two@example.com", true, true)]),
            commentPublisher);
        await limitedDirectoryService.UpdateAsync(current.Id, new("Updated", "M2", "", null, [reviewerOne, reviewerTwo]), true);
        (await service.GetCurrentAsync())!.AssignedReviewers.Should().HaveCount(2);
        await limitedDirectoryService.UpdateAsync(current.Id, new("Updated", "M2", "", null, [reviewerTwo]), true);
        var invalid = () => limitedDirectoryService.UpdateAsync(current.Id,
            new("Updated", "M2", "", null, [reviewerOne, reviewerTwo]), true);
        await invalid.Should().ThrowAsync<ArgumentException>().WithMessage("*inactive or unavailable*");
    }

    [Fact]
    public async Task AdminEditPageRejectsArchivedOrDeletedReviewLinks()
    {
        await CreateAndPublishAsync([reviewerOne]);
        var current = (await service.GetCurrentAsync())!;
        await service.ArchiveAsync(current.Id, true);
        foreach (var id in new[] { current.Id, Guid.NewGuid() })
        {
            var model = CreateModel(service, true);
            SetRole(model, TeamHubUserTypes.Admin);
            model.EditReviewId = id;
            (await model.OnGetAsync(CancellationToken.None)).Should().BeOfType<RedirectToPageResult>();
            model.EditingReview.Should().BeNull();
            model.ErrorMessage.Should().Contain("cannot be edited");
            model.ReviewId = id;
            model.ProjectName = "Edited";
            model.MilestoneName = "M2";
            model.ReviewerIds = [reviewerOne];
            (await model.OnPostEditAsync(CancellationToken.None)).Should().BeOfType<PageResult>();
            model.ModelState.IsValid.Should().BeFalse();
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("unavailable")]
    [InlineData("ticket")]
    [InlineData("project")]
    [InlineData("milestone")]
    [InlineData("description")]
    public async Task EditRejectsInvalidDetailsWithoutChangingReview(string invalid)
    {
        await CreateAndPublishAsync([reviewerOne]);
        var current = (await service.GetCurrentAsync())!;
        var details = new CreateMilestoneReviewRequest("Changed", "Changed", "", null, [reviewerOne]);
        details = invalid switch
        {
            "empty" => details with { ReviewerIds = [] },
            "duplicate" => details with { ReviewerIds = [reviewerOne, reviewerOne] },
            "unavailable" => details with { ReviewerIds = [Guid.NewGuid()] },
            "ticket" => details with { JiraTicketId = "not-a-ticket" },
            "project" => details with { ProjectName = " " },
            "milestone" => details with { MilestoneName = new string('x', 161) },
            _ => details with { Description = new string('x', 4001) }
        };
        var save = () => service.UpdateAsync(current.Id, details, true);
        await save.Should().ThrowAsync<ArgumentException>();
        dbContext.ChangeTracker.Clear();
        var reloaded = (await service.GetCurrentAsync())!;
        reloaded.ProjectName.Should().Be(current.ProjectName);
        reloaded.AssignedReviewers.Should().Equal(current.AssignedReviewers);
    }

    [Theory]
    [InlineData(TeamHubUserTypes.Registered)]
    [InlineData(TeamHubUserTypes.Privileged)]
    public async Task EditHandlersRejectNonAdmins(string role)
    {
        var model = CreateModel(service, true);
        SetRole(model, role);
        model.EditReviewId = Guid.NewGuid();
        (await model.OnGetAsync(CancellationToken.None)).Should().BeOfType<ForbidResult>();
        (await model.OnPostEditAsync(CancellationToken.None)).Should().BeOfType<ForbidResult>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdminEditPageLoadsExistingDetailsAndPreservesInvalidInput(bool published)
    {
        var draft = (await service.CreateDraftAsync(
            new("Project", "Milestone", "**Description**", "GAME-123", [reviewerOne]), "admin@example.com"))!;
        if (published) await service.PublishDraftAsync("admin@example.com");
        var model = CreateModel(service, true);
        SetRole(model, TeamHubUserTypes.Admin);
        model.EditReviewId = draft.Id;
        (await model.OnGetAsync(CancellationToken.None)).Should().BeOfType<PageResult>();
        model.EditingReview!.Id.Should().Be(draft.Id);
        model.ProjectName.Should().Be("Project");
        model.MilestoneName.Should().Be("Milestone");
        model.Description.Should().Be("**Description**");
        model.JiraTicketId.Should().Be("GAME-123");
        model.ReviewerIds.Should().Equal(reviewerOne);
        model.AvailableReviewers.Should().Contain(item => item.Id == reviewerOne);
        model.ProjectName = "Edited";
        model.JiraTicketId = "invalid";
        (await model.OnPostEditAsync(CancellationToken.None)).Should().BeOfType<PageResult>();
        model.ModelState.IsValid.Should().BeFalse();
        model.ProjectName.Should().Be("Edited");
        model.EditingReview.Should().NotBeNull();
        model.ModelState.Clear();
        model.JiraTicketId = null;
        (await model.OnPostEditAsync(CancellationToken.None)).Should().BeOfType<RedirectToPageResult>();
        var saved = published ? await service.GetCurrentAsync() : await service.GetDraftAsync();
        saved!.ProjectName.Should().Be("Edited");
    }

    private static void SetRole(MilestoneReviewModel model, string role) =>
        model.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role)], "Test"));

    [Fact]
    public async Task AdminPageLoadsCascadingProjectMilestoneAndDescriptionOptions()
    {
        var studios = new StubStudioDirectory([
            new StudioDetails { Id = "one", ProjectName = "Project One", IsActive = true },
            new StudioDetails { Id = "duplicate", ProjectName = "project one", IsActive = true },
            new StudioDetails { Id = "two", ProjectName = "Project Two", IsActive = true },
            new StudioDetails { Id = "inactive", ProjectName = "Hidden Project", IsActive = false }
        ]);
        var milestones = new StubMilestoneTracker([
            new MilestoneDto { Title = "Project One", Milestone = "Alpha", Description = "Alpha build" },
            new MilestoneDto { Program = "Project Two", Milestone = "Beta", Description = "Beta build" },
            new MilestoneDto { Title = "Project One", Milestone = "Total MS Complete", Description = "Summary" },
            new MilestoneDto { Title = "Hidden Project", Milestone = "Hidden", Description = "Hidden build" }
        ]);
        var model = CreateModel(service, true, studios: studios, milestones: milestones);
        SetRole(model, TeamHubUserTypes.Admin);

        await model.OnGetAsync(CancellationToken.None);

        model.ProjectOptions.Should().Equal("Project One", "Project Two");
        model.MilestoneOptions.Should().BeEquivalentTo([
            new MilestoneReviewModel.MilestoneReviewSelectionOption("Project One", "Alpha", "Alpha build"),
            new MilestoneReviewModel.MilestoneReviewSelectionOption("Project Two", "Beta", "Beta build")
        ]);
    }

    [Fact]
    public async Task OnGetFeedbackAsync_ReturnsFormattedJsonForLocalMilestoneFeedback()
    {
        await CreateAndPublishAsync([reviewerOne]);
        var current = (await service.GetCurrentAsync())!;
        var row = current.Feedback.Single();
        await service.SaveFeedbackAsync(
            new SaveMilestoneReviewFeedbackRequest(current.Id, row.Id, row.Version, "PC, 1 hour, level 2",
                "Good graphics", "Bugs in level 2", "Audio notes"),
            reviewerOne, isAdmin: false);

        var model = CreateModel(service, allowed: true);
        var result = await model.OnGetFeedbackAsync(current.Id, CancellationToken.None);

        result.Should().BeOfType<JsonResult>();
        var json = (JsonResult)result;
        var jsonText = System.Text.Json.JsonSerializer.Serialize(json.Value);
        jsonText.Should().Contain("Good graphics");
        jsonText.Should().Contain("Bugs in level 2");
        jsonText.Should().Contain("PC, 1 hour, level 2");
        jsonText.Should().Contain("Audio notes");
        jsonText.Should().Contain("Reviewer One");
        using var payload = System.Text.Json.JsonDocument.Parse(jsonText);
        var text = payload.RootElement.GetProperty("comments")[0].GetProperty("parts")[0].GetProperty("text").GetString();
        text.Should().Be("Session Details:\nPC, 1 hour, level 2\n\nWhat's Good:\nGood graphics\n\nNeeds Improvement:\nBugs in level 2\n\nAdditional Feedback:\nAudio notes".Replace("\n", Environment.NewLine));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FeedbackPreviewRendersSafeMarkdownAndPreservesSource(bool archived)
    {
        await service.CreateDraftAsync(
            new CreateMilestoneReviewRequest("Project", "Milestone", "**Build notes**", "GAME-123", [reviewerOne]),
            "admin@example.com");
        await service.PublishDraftAsync("admin@example.com");
        var current = (await service.GetCurrentAsync())!;
        var row = current.Feedback.Single();
        var request = new SaveMilestoneReviewFeedbackRequest(current.Id, row.Id, row.Version,
            "**PC**\n*1 hour*", "==Great art==\n\n- Music\n- Controls",
            "~~Old issue~~\n\n> Improve calibration",
            "[Details](https://example.com)\n<script>alert('xss')</script>\n[Unsafe](javascript:alert(1))");
        await service.SaveFeedbackAsync(request, reviewerOne, isAdmin: false);
        var saved = (await service.GetCurrentAsync())!.Feedback.Single();
        saved.SessionDetails.Should().Be(request.SessionDetails);
        saved.WhatGood.Should().Be(request.WhatGood);
        saved.NeedImprovements.Should().Be(request.NeedImprovements);
        saved.AdditionalFeedback.Should().Be(request.AdditionalFeedback);
        var jira = commentPublisher.Requests.Should().ContainSingle().Subject;
        jira.SessionDetails.Should().Be(request.SessionDetails);
        jira.WhatGood.Should().Be(request.WhatGood);
        jira.NeedImprovements.Should().Be(request.NeedImprovements);
        jira.AdditionalFeedback.Should().Be(request.AdditionalFeedback);
        if (archived) await service.ArchiveAsync(current.Id, isAdmin: true);

        var result = (JsonResult)await CreateModel(service, allowed: true)
            .OnGetFeedbackAsync(current.Id, CancellationToken.None);
        using var payload = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(result.Value));
        var part = payload.RootElement.GetProperty("comments")[0].GetProperty("parts")[0];
        part.GetProperty("kind").GetString().Should().Be("markdown");
        var source = part.GetProperty("text").GetString();
        source.Should().Contain(request.SessionDetails).And.Contain(request.WhatGood)
            .And.Contain(request.NeedImprovements).And.Contain(request.AdditionalFeedback);
        var html = part.GetProperty("html").GetString();
        html.Should().Contain("<strong>PC</strong>").And.Contain("<em>1 hour</em>")
            .And.Contain("<mark>Great art</mark>").And.Contain("<ul><li>Music</li><li>Controls</li></ul>")
            .And.Contain("<del>Old issue</del>").And.Contain("<blockquote>Improve calibration</blockquote>")
            .And.Contain("href='https://example.com'").And.Contain("&lt;script&gt;")
            .And.NotContain("<script>").And.NotContain("href='javascript:");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HistoryPreviewIncludesFeedbackWithOnlyANewSectionFilled(bool sessionOnly)
    {
        await CreateAndPublishAsync([reviewerOne]);
        var current = (await service.GetCurrentAsync())!;
        var row = current.Feedback.Single();
        await service.SaveFeedbackAsync(new SaveMilestoneReviewFeedbackRequest(
            current.Id, row.Id, row.Version,
            sessionOnly ? "PC, 30 minutes" : "", "", "", sessionOnly ? "" : "Audio notes"),
            reviewerOne, isAdmin: false);
        await service.ArchiveAsync(current.Id, isAdmin: true);
        var result = (JsonResult)await CreateModel(service, allowed: true)
            .OnGetFeedbackAsync(current.Id, CancellationToken.None);
        using var payload = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(result.Value));
        var comments = payload.RootElement.GetProperty("comments");
        comments.GetArrayLength().Should().Be(1);
        comments[0].GetProperty("parts")[0].GetProperty("text").GetString()
            .Should().Be(sessionOnly
                ? $"Session Details:{Environment.NewLine}PC, 30 minutes"
                : $"Additional Feedback:{Environment.NewLine}Audio notes");
    }

    [Fact]
    public async Task OnGetFeedbackAsync_ReturnsNotFoundForUnknownReview()
    {
        var model = CreateModel(service, allowed: true);
        var result = await model.OnGetFeedbackAsync(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<JsonResult>();
        var json = (JsonResult)result;
        json.StatusCode.Should().Be(404);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MilestoneJiraPreviewDelegatesTicketScopeResolutionToSharedService(bool image)
    {
        var comments = new StubCommentService();
        var model = CreateModel(service, true, comments);
        model.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity([
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "reader@example.com"),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, TeamHubUserTypes.Privileged)
            ], "Test"));
        var result = image
            ? await model.OnGetImageAsync("SIEHP-1206", "10", "11", CancellationToken.None)
            : await model.OnGetCommentsAsync("SIEHP-1206", CancellationToken.None);
        if (image) result.Should().BeOfType<FileContentResult>();
        else result.Should().BeOfType<JsonResult>();
        comments.LastQuery!.StudioId.Should().BeNull();
        comments.LastQuery.TicketId.Should().Be("SIEHP-1206");
        comments.LastQuery.RequestingUserId.Should().Be("reader@example.com");
        comments.LastQuery.AllowPrivilegedDefaultCredential.Should().BeTrue();
        model.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        var denied = CreateModel(service, false, new StubCommentService());
        (await denied.OnGetCommentsAsync("SIEHP-1206", CancellationToken.None)).Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task RestorePreviewImportsSelectedJiraFeedbackAndNextSaveUpdatesSameComment()
    {
        await service.CreateDraftAsync(new("Test", "Alpha", "", "SIEHP-1206", [reviewerOne]), "admin");
        await service.PublishDraftAsync("admin");
        var current = (await service.GetCurrentAsync())!;
        var oldFeedbackId = Guid.NewGuid();
        var comments = new StubCommentService
        {
            Items = [RestoreComment("41", oldFeedbackId), RestoreComment("42", Guid.NewGuid())]
        };
        var model = CreateModel(service, true, comments);
        SetRole(model, TeamHubUserTypes.Admin);
        model.ReviewId = current.Id;
        (await model.OnPostPreviewRestoreAsync(CancellationToken.None)).Should().BeOfType<PageResult>();
        model.RestorePreview.Should().HaveCount(2);
        comments.GetCommentsCallCount.Should().Be(1);
        comments.LastQuery!.UsePersonalOrDefaultCredential.Should().BeTrue();
        comments.LastQuery.AllowPrivilegedDefaultCredential.Should().BeFalse();
        (await service.GetCurrentAsync())!.Feedback.Single().WhatGood.Should().BeEmpty();
        model.RestoreSelections = [model.RestorePreview.Single(item => item.CommentId == "41").Token];
        (await model.OnPostRestoreAsync(CancellationToken.None)).Should().BeOfType<RedirectToPageResult>();
        dbContext.ChangeTracker.Clear();
        var restored = (await service.GetCurrentAsync())!.Feedback.Single();
        restored.SessionDetails.Should().Be("PC");
        restored.WhatGood.Should().Be("**Great**");
        restored.NeedImprovements.Should().Be("Improve");
        restored.AdditionalFeedback.Should().Be("Art");
        restored.JiraCommentId.Should().Be("41");
        restored.Version.Should().Be(1);
        commentPublisher.Requests.Should().BeEmpty();
        await service.SaveFeedbackAsync(new(current.Id, restored.Id, restored.Version,
            restored.SessionDetails, "Edited", restored.NeedImprovements, restored.AdditionalFeedback), reviewerOne, false);
        commentPublisher.Requests.Single().CommentId.Should().Be("41");
        commentPublisher.Requests.Single().FeedbackId.Should().Be(restored.Id);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("marker")]
    [InlineData("sections")]
    [InlineData("image")]
    [InlineData("link")]
    [InlineData("multiple-markers")]
    public async Task RestorePreviewRejectsUnmatchedOrUnsupportedComments(string invalid)
    {
        await service.CreateDraftAsync(new("Test", "Alpha", "", "SIEHP-1206", [reviewerOne]), "admin");
        await service.PublishDraftAsync("admin");
        var comment = RestoreComment("41", Guid.NewGuid());
        var text = comment.Parts.Single().Text;
        comment = invalid switch
        {
            "email" => comment with { Parts = [new("text", text.Replace("reviewer.one@example.com", "another@example.com"))] },
            "marker" => comment with { Parts = [new("text", text.Replace("TeamHub Milestone Review Feedback:", "Unmanaged:"))] },
            "sections" => comment with { Parts = [new("text", text.Replace("Session Details", "Unknown"))] },
            "multiple-markers" => comment with { Parts = [new("text", text + "\n" + text)] },
            _ => comment with { Parts = [new("text", text), new(invalid, "attachment")] }
        };
        var model = CreateModel(service, true, new StubCommentService { Items = [comment] });
        SetRole(model, TeamHubUserTypes.Admin);
        model.ReviewId = (await service.GetCurrentAsync())!.Id;
        await model.OnPostPreviewRestoreAsync(CancellationToken.None);
        model.RestorePreview.Should().BeEmpty();
        model.RestoreWarnings.Should().ContainSingle();
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("jira-changed")]
    [InlineData("local-changed")]
    [InlineData("ticket-changed")]
    [InlineData("archived")]
    [InlineData("duplicate")]
    public async Task RestoreRejectsStaleOrInvalidSelectionsWithoutOverwritingFeedback(string scenario)
    {
        await service.CreateDraftAsync(new("Test", "Alpha", "", "SIEHP-1206", [reviewerOne]), "admin");
        await service.PublishDraftAsync("admin");
        var current = (await service.GetCurrentAsync())!;
        var comments = new StubCommentService { Items = [RestoreComment("41", Guid.NewGuid())] };
        var model = CreateModel(service, true, comments);
        SetRole(model, TeamHubUserTypes.Admin);
        model.ReviewId = current.Id;
        await model.OnPostPreviewRestoreAsync(CancellationToken.None);
        var token = model.RestorePreview.Single().Token;
        model.RestoreSelections = [token];
        switch (scenario)
        {
            case "tampered": model.RestoreSelections = ["not-a-token"]; break;
            case "jira-changed": comments.Items = []; break;
            case "duplicate": model.RestoreSelections = [token, token]; break;
            case "local-changed":
                var row = current.Feedback.Single();
                await service.SaveFeedbackAsync(new(current.Id, row.Id, row.Version, "", "Existing", "", ""), reviewerOne, false);
                break;
            case "ticket-changed":
                await service.UpdateAsync(current.Id, new("Test", "Alpha", "", "SIEHP-1207", [reviewerOne]), true);
                break;
            case "archived": await service.ArchiveAsync(current.Id, true); break;
        }
        await model.OnPostRestoreAsync(CancellationToken.None);
        model.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        dbContext.ChangeTracker.Clear();
        var saved = scenario == "archived"
            ? (await service.GetHistoryAsync()).Single() : (await service.GetCurrentAsync())!;
        saved.Feedback.Single().WhatGood.Should().Be(scenario == "local-changed" ? "Existing" : "");
    }

    [Fact]
    public async Task RestoreActionsRequireAdminAndAnAvailableJiraCredential()
    {
        var denied = CreateModel(service, true);
        SetRole(denied, TeamHubUserTypes.Registered);
        (await denied.OnPostPreviewRestoreAsync(CancellationToken.None)).Should().BeOfType<ForbidResult>();
        (await denied.OnPostRestoreAsync(CancellationToken.None)).Should().BeOfType<ForbidResult>();
        var restore = () => service.RestoreFeedbackAsync([], false);
        await restore.Should().ThrowAsync<UnauthorizedAccessException>();
        await service.CreateDraftAsync(new("Test", "Alpha", "", "SIEHP-1206", [reviewerOne]), "admin");
        await service.PublishDraftAsync("admin");
        var model = CreateModel(service, true, new StubCommentService { Error = new StudioJiraCommentException("No default Jira bearer token") });
        SetRole(model, TeamHubUserTypes.Admin);
        model.ReviewId = (await service.GetCurrentAsync())!.Id;
        await model.OnPostPreviewRestoreAsync(CancellationToken.None);
        model.RestorePreview.Should().BeEmpty();
        model.RestoreWarnings.Single().Should().Contain("default Jira bearer token");
    }

    [Fact]
    public async Task RestorePreviewUnderstandsRetrievedBoldHeadingsAndSkipsNonemptyRows()
    {
        await service.CreateDraftAsync(new("Test", "Alpha", "", "SIEHP-1206", [reviewerOne, reviewerTwo]), "admin");
        await service.PublishDraftAsync("admin");
        var current = (await service.GetCurrentAsync())!;
        var row = current.Feedback.Single(item => item.ReviewerId == reviewerTwo);
        await service.SaveFeedbackAsync(new(current.Id, row.Id, row.Version, "", "Existing", "", ""), reviewerTwo, false);
        var comment = RestoreComment("41", Guid.NewGuid());
        using var body = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(comment.Parts.Single().Text));
        var formatted = comment with { Parts = JiraCommentBodyParser.Parse(body.RootElement, []) };
        var model = CreateModel(service, true, new StubCommentService { Items = [formatted] });
        SetRole(model, TeamHubUserTypes.Admin);
        model.ReviewId = current.Id;
        await model.OnPostPreviewRestoreAsync(CancellationToken.None);
        model.RestorePreview.Should().ContainSingle().Which.WhatGood.Should().Be("**Great**");
        model.RestoreWarnings.Single().Should().Contain("will not be overwritten");
    }

    [Fact]
    public async Task RestoreServiceCommitsAllSelectedRowsOrNone()
    {
        await service.CreateDraftAsync(new("Test", "Alpha", "", "SIEHP-1206", [reviewerOne, reviewerTwo]), "admin");
        await service.PublishDraftAsync("admin");
        var current = (await service.GetCurrentAsync())!;
        var first = current.Feedback.Single(item => item.ReviewerId == reviewerOne);
        var second = current.Feedback.Single(item => item.ReviewerId == reviewerTwo);
        var requests = new[]
        {
            new RestoreMilestoneReviewFeedbackRequest(current.Id, "SIEHP-1206", first.Id, first.Version,
                "41", "PC", "Good", "", ""),
            new RestoreMilestoneReviewFeedbackRequest(current.Id, "SIEHP-1206", second.Id, second.Version + 1,
                "42", "", "Good", "", "")
        };
        var invalid = () => service.RestoreFeedbackAsync(requests, true);
        await invalid.Should().ThrowAsync<InvalidOperationException>();
        dbContext.ChangeTracker.Clear();
        (await service.GetCurrentAsync())!.Feedback.Should().OnlyContain(item => item.WhatGood == "" && item.JiraCommentId == null);
        requests[1] = requests[1] with { Version = second.Version };
        await service.RestoreFeedbackAsync(requests, true);
        dbContext.ChangeTracker.Clear();
        (await service.GetCurrentAsync())!.Feedback.Should().OnlyContain(item => item.WhatGood == "Good" && item.Version == 1);
        commentPublisher.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RestoreRejectsExpiredProtectedPreview()
    {
        await service.CreateDraftAsync(new("Test", "Alpha", "", "SIEHP-1206", [reviewerOne]), "admin");
        await service.PublishDraftAsync("admin");
        var protection = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        var model = CreateModel(service, true,
            new StubCommentService { Items = [RestoreComment("41", Guid.NewGuid())] }, protection);
        SetRole(model, TeamHubUserTypes.Admin);
        model.ReviewId = (await service.GetCurrentAsync())!.Id;
        await model.OnPostPreviewRestoreAsync(CancellationToken.None);
        var protector = protection.CreateProtector("TeamHub.MilestoneReview.Restore.v1");
        var token = System.Text.Json.Nodes.JsonNode.Parse(
            Microsoft.AspNetCore.DataProtection.DataProtectionCommonExtensions.Unprotect(protector, model.RestorePreview.Single().Token))!;
        var expires = token["Expires"]!.GetValue<DateTimeOffset>();
        expires.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(15), TimeSpan.FromSeconds(5));
        token["Expires"] = DateTimeOffset.UtcNow.AddSeconds(-1);
        model.RestoreSelections = [Microsoft.AspNetCore.DataProtection.DataProtectionCommonExtensions.Protect(protector, token.ToJsonString())];
        await model.OnPostRestoreAsync(CancellationToken.None);
        model.ErrorMessage.Should().Contain("expired");
        (await service.GetCurrentAsync())!.Feedback.Single().JiraCommentId.Should().BeNull();
    }

    private static StudioJiraComment RestoreComment(string commentId, Guid oldFeedbackId) =>
        new(commentId, "Reviewer One", DateTimeOffset.UtcNow, null, [new("text", $"""
            *Reviewer*: Reviewer One
            *Reviewer email*: reviewer.one@example.com

            *Session Details*
            PC

            *What's Good*
            **Great**

            *Needs Improvement*
            Improve

            *Additional Feedback*
            Art

            [TeamHub Milestone Review Feedback: {oldFeedbackId:D}]
            """)]);

    [Theory]
    [InlineData(AccessLevel.ReadOnly, false)]
    [InlineData(AccessLevel.Create, false)]
    [InlineData(AccessLevel.Edit, true)]
    public async Task FeedbackEditingRequiresConfiguredEditAccess(AccessLevel level, bool canEdit)
    {
        await CreateAndPublishAsync([reviewerOne]);
        var row = (await service.GetCurrentAsync())!.Feedback.Single();
        var model = CreateModel(service, true, level: level, user: ReviewUser());

        await model.OnGetAsync(CancellationToken.None);
        model.CanEdit(row).Should().Be(canEdit);
        model.IsEditingFeedback(row).Should().BeFalse();
        model.EditFeedbackId = row.Id;
        var result = await model.OnGetAsync(CancellationToken.None);
        if (canEdit) result.Should().BeOfType<PageResult>();
        else result.Should().BeOfType<ForbidResult>();
        model.IsEditingFeedback(row).Should().Be(canEdit);

        model.ReviewId = model.CurrentReview!.Id;
        model.FeedbackId = row.Id;
        model.Version = row.Version;
        model.WhatGood = "Saved feedback";
        var save = await model.OnPostSaveFeedbackAsync(CancellationToken.None);
        if (canEdit) save.Should().BeOfType<RedirectToPageResult>();
        else save.Should().BeOfType<ForbidResult>();
        (await service.GetCurrentAsync())!.Feedback.Single().WhatGood.Should()
            .Be(canEdit ? "Saved feedback" : "");
    }

    [Fact]
    public async Task SavedFeedbackLocksUntilEditIsRequestedAndLocksAgainAfterSaving()
    {
        await CreateAndPublishAsync([reviewerOne, reviewerTwo]);
        var current = (await service.GetCurrentAsync())!;
        var row = current.Feedback.Single(item => item.ReviewerId == reviewerOne);
        await service.SaveFeedbackAsync(new(current.Id, row.Id, row.Version, "PC", "Good", "", ""), reviewerOne, false);
        var model = CreateModel(service, true, level: AccessLevel.Edit, user: ReviewUser());
        await model.OnGetAsync(CancellationToken.None);
        row = model.CurrentReview!.Feedback.Single(item => item.Id == row.Id);
        model.CanEdit(row).Should().BeTrue();
        model.IsEditingFeedback(row).Should().BeFalse();

        model.EditFeedbackId = model.CurrentReview.Feedback.Single(item => item.ReviewerId == reviewerTwo).Id;
        (await model.OnGetAsync(CancellationToken.None)).Should().BeOfType<ForbidResult>();
        model.EditFeedbackId = row.Id;
        (await model.OnGetAsync(CancellationToken.None)).Should().BeOfType<PageResult>();
        model.IsEditingFeedback(row).Should().BeTrue();
        model.ReviewId = current.Id;
        model.FeedbackId = row.Id;
        model.Version = row.Version;
        model.WhatGood = "Updated";
        (await model.OnPostSaveFeedbackAsync(CancellationToken.None)).Should().BeOfType<RedirectToPageResult>();

        var reloaded = CreateModel(service, true, level: AccessLevel.Edit, user: ReviewUser());
        await reloaded.OnGetAsync(CancellationToken.None);
        var saved = reloaded.CurrentReview!.Feedback.Single(item => item.Id == row.Id);
        saved.WhatGood.Should().Be("Updated");
        reloaded.IsEditingFeedback(saved).Should().BeFalse();
    }

    private AuthorizedUserRecord ReviewUser() => new(reviewerOne, "reviewer.one@example.com", null,
        "Reviewer One", TeamHubUserTypes.Registered, true, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static MilestoneReviewModel CreateModel(IMilestoneReviewService reviewService, bool allowed,
        StubCommentService? comments = null, Microsoft.AspNetCore.DataProtection.IDataProtectionProvider? protection = null,
        AccessLevel? level = null, AuthorizedUserRecord? user = null,
        IStudioDirectoryService? studios = null, IMilestoneTrackerService? milestones = null) =>
        new(reviewService,
            new StubUserDirectory([]),
            new StubAccess(allowed, level, user),
            comments ?? new StubCommentService(),
            protection ?? new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),
            new TeamHub.Web.Formatting.SafeMarkdownRenderer(),
            NullLogger<MilestoneReviewModel>.Instance,
            studios,
            milestones)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() }
        };

    private sealed class StubAccess(bool allowed, AccessLevel? level = null, AuthorizedUserRecord? user = null) : ICurrentAccessService
    {
        public Task<bool> CanAsync(string module, AccessLevel required = AccessLevel.ReadOnly,
            CancellationToken cancellationToken = default) => Task.FromResult(allowed && (!level.HasValue || level.Value.Allows(required)));
        public Task<AccessLevel> GetAccessLevelAsync(string module, CancellationToken cancellationToken = default) =>
            Task.FromResult(allowed ? AccessLevel.ReadOnly : AccessLevel.NoAccess);
        public Task<CurrentAccessSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CurrentAccessSnapshot(TeamHubUserTypes.Registered, user));
    }

    private sealed class StubExternalProcessRunner : IExternalProcessRunner
    {
        public ExternalProcessResult Result { get; set; } = new("{}", "", 0);
        public List<ExternalProcessRequest> Requests { get; } = [];
        public bool IsEnabled(string processKey) => true;
        public Task<ExternalProcessResult> RunAsync(
            ExternalProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }

    private sealed class StubExternalProcessCredentialResolver : IExternalProcessCredentialResolver
    {
        public string? ProcessKey { get; private set; }
        public string? RequestingUserId { get; private set; }
        public bool? AllowPrivilegedDefaultCredential { get; private set; }
        public Task<IReadOnlyDictionary<string, string>> ResolveEnvironmentAsync(
            string processKey,
            string requestingUserId,
            bool allowPrivilegedDefaultCredential,
            CancellationToken cancellationToken = default)
        {
            ProcessKey = processKey;
            RequestingUserId = requestingUserId;
            AllowPrivilegedDefaultCredential = allowPrivilegedDefaultCredential;
            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string> { ["TEAMHUB_JIRA_PAT"] = "secret" });
        }
    }

    private sealed class StubAtlassianCredentials : IAtlassianCredentialAccessor
    {
        public string? RequestingUserId { get; private set; }
        public bool? AllowDefault { get; private set; }

        public Task<AtlassianResolvedCredential?> ResolveJiraCredentialAsync(
            string userId, bool allowPrivilegedDefault, CancellationToken cancellationToken = default)
        {
            RequestingUserId = userId;
            AllowDefault = allowPrivilegedDefault;
            return Task.FromResult<AtlassianResolvedCredential?>(
                new("configured-admin-token", false, true));
        }

        public Task<AtlassianResolvedCredential?> ResolveDefaultJiraCredentialAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AtlassianResolvedCredential?>(new("default-token", true, true));

        public Task<AtlassianResolvedCredential?> ResolveConfluenceCredentialAsync(
            string userId, bool allowPrivilegedDefault, CancellationToken cancellationToken = default) =>
            Task.FromResult<AtlassianResolvedCredential?>(null);
    }

    private sealed class StubCommentService : IStudioJiraCommentService
    {
        public IReadOnlyList<StudioJiraComment> Items { get; set; } = [];
        public Exception? Error { get; set; }
        public StudioJiraCommentQuery? LastQuery { get; private set; }
        public int GetCommentsCallCount { get; private set; }
        public Task<StudioJiraCommentResult> GetCommentsAsync(StudioJiraCommentQuery query, CancellationToken cancellationToken = default)
        {
            GetCommentsCallCount++;
            LastQuery = query;
            if (Error is not null) throw Error;
            return Task.FromResult(new StudioJiraCommentResult(query.TicketId, "https://jira.example.test", false, Items));
        }

        public Task<StudioJiraCommentImage> GetImageAsync(StudioJiraCommentQuery query, string commentId, string attachmentId, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(new StudioJiraCommentImage([], "image/png"));
        }

        public Task<SaveStudioJiraCommentResult> SaveCommentAsync(
            SaveStudioJiraCommentRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SaveStudioJiraCommentResult(request.CommentId ?? "42"));

        public Task<StudioJiraMilestoneReviewCommentResult> UpsertMilestoneReviewCommentAsync(StudioJiraMilestoneReviewCommentRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StudioJiraMilestoneReviewCommentResult("42"));
    }

    private sealed class StubStudioDirectory(IReadOnlyList<StudioDetails> studios) : IStudioDirectoryService
    {
        public Task<IReadOnlyList<StudioDetails>> GetStudiosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(studios);

        public Task<StudioDetails?> GetStudioAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(studios.FirstOrDefault(item => item.Id == id));

        public Task<StudioDetails> SaveStudioAsync(StudioDetails studio, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteStudioAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubMilestoneTracker(IReadOnlyList<MilestoneDto> milestones) : IMilestoneTrackerService
    {
        public Task<IReadOnlyList<MilestoneDto>> GetMilestonesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(milestones);

        public Task<MilestoneSourceTestResult> TestSourceAsync(
            MilestoneSourceSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MilestoneSourceTestResult(milestones.Count));
    }

}
