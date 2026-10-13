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
                await scope.ServiceProvider.GetRequiredService<IMilestoneReviewDatabaseInitializer>().InitializeAsync();
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
    public async Task PublishCreatesSharedRowsAndOnlyAssignedReviewerCanEditOwnRow()
    {
        await CreateAndPublishAsync([reviewerOne, reviewerTwo]);
        var current = await service.GetCurrentAsync();

        current.Should().NotBeNull();
        current!.Feedback.Should().HaveCount(2);
        current.Feedback.Select(row => row.Reviewer).Should().BeEquivalentTo("Reviewer One", "Reviewer Two");

        var row = current.Feedback.Single(item => item.ReviewerId == reviewerOne);
        var save = new SaveMilestoneReviewFeedbackRequest(
            current.Id, row.Id, row.Version, "Good", "Bad", "Improve");
        await service.SaveFeedbackAsync(save, reviewerOne, isAdmin: false);
        var updated = (await service.GetCurrentAsync())!.Feedback.Single(item => item.Id == row.Id);
        updated.WhatGood.Should().Be("Good");
        updated.Version.Should().Be(row.Version + 1);
        await using (var reloadedContext = new MilestoneReviewDbContext(
            new DbContextOptionsBuilder<MilestoneReviewDbContext>().UseSqlite(connection).Options))
        {
            var reloaded = await new MilestoneReviewService(
                reloadedContext, new StubUserDirectory([]), commentPublisher).GetCurrentAsync();
            reloaded!.Feedback.Single(item => item.Id == row.Id).WhatGood.Should().Be("Good");
        }

        var otherUser = () => service.SaveFeedbackAsync(
            save with { Version = updated.Version, WhatGood = "Overwrite" }, reviewerTwo, isAdmin: false);
        await otherUser.Should().ThrowAsync<UnauthorizedAccessException>();
        (await service.GetCurrentAsync())!.Feedback.Single(item => item.Id == row.Id).WhatGood.Should().Be("Good");
    }

    [Fact]
    public async Task AdminCanEditAnyAssignedRowAndStaleVersionsAreRejected()
    {
        await CreateAndPublishAsync([reviewerOne]);
        var current = (await service.GetCurrentAsync())!;
        var row = current.Feedback.Single();
        var request = new SaveMilestoneReviewFeedbackRequest(
            current.Id, row.Id, row.Version, "First save", "", "");
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
            new SaveMilestoneReviewFeedbackRequest(old.Id, oldFeedback.Id, oldFeedback.Version, "Late", "", ""),
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
            new SaveMilestoneReviewFeedbackRequest(current.Id, fb.Id, fb.Version, "M1 Good", "M1 Bad", "M1 Improve"),
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
            current.Id, row.Id, row.Version, "Good", "Bad", "Improve");
        await service.SaveFeedbackAsync(firstSave, reviewerOne, isAdmin: false);
        var saved = (await service.GetCurrentAsync())!.Feedback.Single();
        saved.JiraCommentId.Should().Be("42");
        commentPublisher.Requests.Should().ContainSingle();
        commentPublisher.Requests[0].RequestingUserId.Should().Be("reviewer.one@example.com");
        commentPublisher.Requests[0].TicketId.Should().Be("GAME-123");

        await service.SaveFeedbackAsync(
            firstSave with { Version = saved.Version, WhatGood = "Updated good" },
            reviewerOne, isAdmin: false);
        commentPublisher.Requests.Should().HaveCount(2);
        commentPublisher.Requests[1].CommentId.Should().Be("42");
        commentPublisher.Requests[1].WhatGood.Should().Be("Updated good");
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
            new SaveMilestoneReviewFeedbackRequest(current.Id, row.Id, row.Version, "Saved locally", "", ""),
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
    public async Task OnGetFeedbackAsync_ReturnsFormattedJsonForLocalMilestoneFeedback()
    {
        await CreateAndPublishAsync([reviewerOne]);
        var current = (await service.GetCurrentAsync())!;
        var row = current.Feedback.Single();
        await service.SaveFeedbackAsync(
            new SaveMilestoneReviewFeedbackRequest(current.Id, row.Id, row.Version, "Good graphics", "Bugs in level 2", "Add tutorial"),
            reviewerOne, isAdmin: false);

        var model = CreateModel(service, allowed: true);
        var result = await model.OnGetFeedbackAsync(current.Id, CancellationToken.None);

        result.Should().BeOfType<JsonResult>();
        var json = (JsonResult)result;
        var jsonText = System.Text.Json.JsonSerializer.Serialize(json.Value);
        jsonText.Should().Contain("Good graphics");
        jsonText.Should().Contain("Bugs in level 2");
        jsonText.Should().Contain("Add tutorial");
        jsonText.Should().Contain("Reviewer One");
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

    private static MilestoneReviewModel CreateModel(IMilestoneReviewService reviewService, bool allowed) =>
        new(reviewService,
            new StubUserDirectory([]),
            new StubAccess(allowed),
            new StubCommentService(),
            new StubStudioDirectory(),
            new StubConfigurationService(),
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
        public Task<StudioJiraCommentResult> GetCommentsAsync(StudioJiraCommentQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StudioJiraCommentResult(query.TicketId, "https://jira.example.test", false, []));

        public Task<StudioJiraCommentImage> GetImageAsync(StudioJiraCommentQuery query, string commentId, string attachmentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StudioJiraCommentImage([], "image/png"));

        public Task<StudioJiraMilestoneReviewCommentResult> UpsertMilestoneReviewCommentAsync(StudioJiraMilestoneReviewCommentRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StudioJiraMilestoneReviewCommentResult("42"));
    }

    private sealed class StubStudioDirectory : IStudioDirectoryService
    {
        public Task<IReadOnlyList<StudioDetails>> GetStudiosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StudioDetails>>([]);
        public Task<StudioDetails?> GetStudioAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<StudioDetails?>(null);
        public Task<StudioDetails> SaveStudioAsync(StudioDetails studio, CancellationToken cancellationToken = default) =>
            Task.FromResult(studio);
        public Task DeleteStudioAsync(string id, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubConfigurationService : IAtlassianConfigurationService
    {
        public Task<AtlassianIntegrationSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AtlassianIntegrationSettings());
        public Task SaveSettingsAsync(AtlassianIntegrationSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<StudioAtlassianMapping>> ListStudioMappingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StudioAtlassianMapping>>([]);
        public Task<StudioAtlassianMapping?> GetStudioMappingAsync(string studioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<StudioAtlassianMapping?>(null);
        public Task SaveStudioMappingsAsync(IReadOnlyCollection<StudioAtlassianMapping> mappings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<AtlassianConnectionStatus> GetConnectionStatusAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AtlassianConnectionStatus(false, null, false, null));
        public Task SaveUserTokensAsync(string userId, string? jiraToken, string? confluenceToken, bool removeJiraToken = false, bool removeConfluenceToken = false, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<AtlassianDefaultCredentialStatus> GetDefaultCredentialStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AtlassianDefaultCredentialStatus(false, false));
        public Task SaveDefaultTokensAsync(string? jiraToken, string? confluenceToken, bool removeJiraToken = false, bool removeConfluenceToken = false, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<AtlassianPrivilegedAccess>> ListPrivilegedAccessAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AtlassianPrivilegedAccess>>([]);
        public Task SavePrivilegedAccessAsync(IReadOnlyCollection<AtlassianPrivilegedAccess> access, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task MoveUserTokensAsync(string currentUserId, string newUserId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteUserTokensAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
