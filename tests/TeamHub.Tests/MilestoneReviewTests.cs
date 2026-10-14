using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TeamHub.Authentication;
using TeamHub.MilestoneReview;
using TeamHub.Studio;
using TeamHub.Web.AccessControl;
using TeamHub.Web.Pages;

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
                addedColumns.Should().Contain("JiraTicketId", "ReviewerUserId", "JiraCommentId", "JiraSyncError");
                addedColumns.Should().Contain("SessionDetails", "AdditionalFeedback");
                addedColumns.Should().NotContain("WhatsBad");
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
            (await upgradedService.GetHistoryAsync()).Should().ContainSingle()
                .Which.Feedback.Single().SessionDetails.Should().Be("PC");
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
    public async Task PublishingNewReviewReplacesCurrentButRetainsPreviousReview()
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

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM MilestoneReviews WHERE Id = $id AND Status = $status";
        command.Parameters.AddWithValue("$id", old.Id);
        command.Parameters.AddWithValue("$status", (int)MilestoneReviewStatus.Superseded);
        Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(1);
        var oldFeedback = old.Feedback.Single();
        var oldSave = () => service.SaveFeedbackAsync(
            new SaveMilestoneReviewFeedbackRequest(old.Id, oldFeedback.Id, oldFeedback.Version, "", "Late", "", ""),
            reviewerOne, isAdmin: true);
        await oldSave.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*current published review*");

        var history = await service.GetHistoryAsync();
        history.Should().HaveCount(1);
        history[0].Id.Should().Be(old.Id);
        history[0].MilestoneName.Should().Be("First milestone");
        history[0].Feedback.Should().HaveCount(1);
        history[0].Feedback[0].ReviewerId.Should().Be(reviewerOne);
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

        var historyAfterSecond = await service.GetHistoryAsync();
        historyAfterSecond.Should().HaveCount(1);
        historyAfterSecond[0].MilestoneName.Should().Be("Milestone 1");
        historyAfterSecond[0].Feedback.Single().WhatGood.Should().Be("M1 Good");

        await service.CreateDraftAsync(
            new CreateMilestoneReviewRequest("Project", "Milestone 3", "M3 Desc", "TICK-3", [reviewerOne]),
            "admin@example.com");
        await service.PublishDraftAsync("admin@example.com");

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
        await lateSave.Should().ThrowAsync<InvalidOperationException>().WithMessage("*current published review*");

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

    private sealed class StubUserDirectory(IReadOnlyList<MilestoneReviewUser> users) : IMilestoneReviewUserDirectory
    {
        public Task<IReadOnlyList<MilestoneReviewUser>> GetActiveUsersAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(users);
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

    private static MilestoneReviewModel CreateModel(IMilestoneReviewService reviewService, bool allowed,
        StubCommentService? comments = null) =>
        new(reviewService,
            new StubUserDirectory([]),
            new StubAccess(allowed),
            comments ?? new StubCommentService(),
            new TeamHub.Web.Formatting.SafeMarkdownRenderer(),
            NullLogger<MilestoneReviewModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() }
        };

    private sealed class StubAccess(bool allowed) : ICurrentAccessService
    {
        public Task<bool> CanAsync(string module, AccessLevel required = AccessLevel.ReadOnly,
            CancellationToken cancellationToken = default) => Task.FromResult(allowed);
        public Task<AccessLevel> GetAccessLevelAsync(string module, CancellationToken cancellationToken = default) =>
            Task.FromResult(allowed ? AccessLevel.ReadOnly : AccessLevel.NoAccess);
        public Task<CurrentAccessSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CurrentAccessSnapshot(TeamHubUserTypes.Registered, null));
    }

    private sealed class StubCommentService : IStudioJiraCommentService
    {
        public StudioJiraCommentQuery? LastQuery { get; private set; }
        public Task<StudioJiraCommentResult> GetCommentsAsync(StudioJiraCommentQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(new StudioJiraCommentResult(query.TicketId, "https://jira.example.test", false, []));
        }

        public Task<StudioJiraCommentImage> GetImageAsync(StudioJiraCommentQuery query, string commentId, string attachmentId, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(new StudioJiraCommentImage([], "image/png"));
        }

        public Task<StudioJiraMilestoneReviewCommentResult> UpsertMilestoneReviewCommentAsync(StudioJiraMilestoneReviewCommentRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StudioJiraMilestoneReviewCommentResult("42"));
    }

}
