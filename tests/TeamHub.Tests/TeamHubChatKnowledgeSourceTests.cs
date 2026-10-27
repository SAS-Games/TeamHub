using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using TeamHub.AI.Chat.Contracts;
using TeamHub.AI.Contracts;
using TeamHub.Application.Interfaces;
using TeamHub.Application.Models;
using TeamHub.Authentication;
using TeamHub.Domain.Enums;
using TeamHub.Milestones;
using TeamHub.MilestoneReview;
using TeamHub.Studio;
using TeamHub.Team;
using TeamHub.Web.AccessControl;
using TeamHub.Web.AI;
using TeamHub.Web.ExternalProcesses.WorklogAnalytics;
using TeamHub.Web.Pages;
using Review = TeamHub.MilestoneReview.MilestoneReview;

namespace TeamHub.Tests;

public sealed class TeamHubChatKnowledgeSourceTests
{
    [Fact]
    public async Task Chat_page_requests_consent_before_calling_the_model_for_external_support_sources()
    {
        var user = new AuthorizedUserRecord(
            Guid.NewGuid(), "user@example.com", null, "Test User", TeamHubUserTypes.Registered,
            true, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var chat = new CapturingChatService();
        var page = new AIChatModel(
            chat,
            new ModuleAccessService(user, TeamHubModules.SupportSummary),
            null!);

        var result = await page.OnPostMessageAsync(new AIChatModel.ChatInput
        {
            Mode = nameof(AiChatMode.TeamHub),
            Model = "gpt-5",
            Messages = [new AiChatMessage("user", "What support activities are currently going on?")]
        }, CancellationToken.None);

        chat.CapturedUser.Should().BeNull();
        var json = JsonSerializer.Serialize(((JsonResult)result).Value);
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("requiresExternalAccess").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("externalSources").EnumerateArray()
            .Select(item => item.GetString())
            .Should().Equal(AiChatExternalSources.Jira, AiChatExternalSources.Confluence);
    }

    [Fact]
    public async Task Chat_page_passes_only_the_approved_sources_for_the_current_question()
    {
        var user = new AuthorizedUserRecord(
            Guid.NewGuid(), "user@example.com", null, "Test User", TeamHubUserTypes.Registered,
            true, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var chat = new CapturingChatService();
        var page = new AIChatModel(
            chat,
            new ModuleAccessService(user, TeamHubModules.SupportSummary),
            null!);

        await page.OnPostMessageAsync(new AIChatModel.ChatInput
        {
            Mode = nameof(AiChatMode.TeamHub),
            Model = "gpt-5",
            Messages = [new AiChatMessage("user", "Show the open Jira tickets")],
            ExternalAccessDecision = "Approved",
            ApprovedExternalSources = [AiChatExternalSources.Jira, AiChatExternalSources.Confluence]
        }, CancellationToken.None);

        chat.CapturedUser.Should().NotBeNull();
        chat.CapturedUser!.HasApprovedExternalSource(AiChatExternalSources.Jira).Should().BeTrue();
        chat.CapturedUser.HasApprovedExternalSource(AiChatExternalSources.Confluence).Should().BeFalse();
    }

    [Fact]
    public async Task Team_mode_includes_matching_visible_team_directory_members()
    {
        var user = new AuthorizedUserRecord(
            Guid.NewGuid(), "user@example.com", null, "Test User", TeamHubUserTypes.Registered,
            true, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var directory = new TeamDirectoryDto
        {
            Members =
            [
                new TeamMemberDto
                {
                    Id = "ramu",
                    EmployeeName = "Ramachandran, Ramu",
                    Role = "Director",
                    Email = "ramu.ramachandran@sony.com",
                    Section = TeamMemberSections.Management
                }
            ]
        };
        var source = new TeamHubChatKnowledgeSource(
            new TeamOnlyAccessService(user),
            new EmptyMilestoneService(),
            new EmptyStudioService(),
            new EmptyReviewService(),
            new EmptyWorkflowService(),
            new StubTeamDirectoryService(directory));

        var result = await source.SearchAsync(
            new AiChatUserContext(user.UserId, user.DisplayName, user.UserType),
            "Who is Ramu?");

        var team = result.Should().ContainSingle(item => item.Id == "teamhub-team-directory").Subject;
        team.Title.Should().Be("TeamHub team directory");
        team.Content.Should().Contain("Ramachandran, Ramu");
        team.Content.Should().Contain("Director");
        team.Content.Should().Contain("ramu.ramachandran@sony.com");
    }

    [Fact]
    public async Task Team_mode_uses_the_support_summary_jira_and_confluence_sources()
    {
        var user = new AuthorizedUserRecord(
            Guid.NewGuid(), "user@example.com", null, "Test User", TeamHubUserTypes.Registered,
            true, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var studio = new StudioDetails
        {
            Id = "studio-1",
            StudioName = "Example Studio",
            ProjectName = "Example Project",
            IsActive = true
        };
        var source = new SupportSummaryChatKnowledgeSource(
            new ModuleAccessService(user, TeamHubModules.SupportSummary),
            new StubStudioService(studio),
            new StubMilestoneService(),
            new StubPlanningService(),
            new StubJiraService(),
            new StubConfluenceService(),
            new UnusedEffortService());

        var result = await source.SearchAsync(
            new AiChatUserContext(
                user.UserId,
                user.DisplayName,
                user.UserType,
                new HashSet<string>(
                    [AiChatExternalSources.Jira, AiChatExternalSources.Confluence],
                    StringComparer.OrdinalIgnoreCase)),
            "What is in Support Summary?");

        result.Should().Contain(item => item.Id == "teamhub-support-expected-deliveries");
        result.Should().Contain(item => item.Id == "teamhub-support-build-reviews");
        result.Single(item => item.Id == "teamhub-support-jira").Content.Should().Contain("TEAM-42");
        result.Single(item => item.Id == "teamhub-support-confluence").Content.Should().Contain("Current studio work");
    }

    [Fact]
    public async Task Team_mode_does_not_access_jira_or_confluence_without_question_consent()
    {
        var user = new AuthorizedUserRecord(
            Guid.NewGuid(), "user@example.com", null, "Test User", TeamHubUserTypes.Registered,
            true, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var source = new SupportSummaryChatKnowledgeSource(
            new ModuleAccessService(user, TeamHubModules.SupportSummary),
            new StubStudioService(new StudioDetails
            {
                Id = "studio-1",
                StudioName = "Example Studio",
                ProjectName = "Example Project",
                IsActive = true
            }),
            new StubMilestoneService(),
            new StubPlanningService(),
            new ThrowingJiraService(),
            new ThrowingConfluenceService(),
            new UnusedEffortService());

        var result = await source.SearchAsync(
            new AiChatUserContext(user.UserId, user.DisplayName, user.UserType),
            "What support activities are currently going on?");

        result.Should().Contain(item => item.Id == "teamhub-support-expected-deliveries");
        result.Should().Contain(item => item.Id == "teamhub-support-build-reviews");
        result.Should().NotContain(item => item.Id == "teamhub-support-jira");
        result.Should().NotContain(item => item.Id == "teamhub-support-confluence");
        result.Single(item => item.Id == "teamhub-support-jira-consent").Content
            .Should().Contain("did not approve Jira");
        result.Single(item => item.Id == "teamhub-support-confluence-consent").Content
            .Should().Contain("did not approve Confluence");
    }

    [Fact]
    public void Support_questions_request_only_the_relevant_external_connections()
    {
        SupportSummaryChatKnowledgeSource.GetRequestedExternalSources(
                "What support activities are currently going on?")
            .Should().BeEquivalentTo([AiChatExternalSources.Jira, AiChatExternalSources.Confluence]);
        SupportSummaryChatKnowledgeSource.GetRequestedExternalSources("Show the open Jira tickets")
            .Should().Equal(AiChatExternalSources.Jira);
        SupportSummaryChatKnowledgeSource.GetRequestedExternalSources("Show the weekly Confluence update")
            .Should().Equal(AiChatExternalSources.Confluence);
    }

    [Fact]
    public async Task Team_mode_routes_a_broad_milestone_tracker_question_to_milestone_data()
    {
        var user = new AuthorizedUserRecord(
            Guid.NewGuid(), "user@example.com", null, "Test User", TeamHubUserTypes.Registered,
            true, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var source = new TeamHubChatKnowledgeSource(
            new ModuleAccessService(user, TeamHubModules.Milestones),
            new StubMilestoneService(),
            new EmptyStudioService(),
            new EmptyReviewService(),
            new EmptyWorkflowService(),
            new StubTeamDirectoryService(new TeamDirectoryDto()));

        var result = await source.SearchAsync(
            new AiChatUserContext(user.UserId, user.DisplayName, user.UserType),
            "Can you provide info from Milestone Tracker?");

        var milestones = result.Should().ContainSingle(item => item.Id == "teamhub-milestones").Subject;
        milestones.Content.Should().Contain("India Hero");
        milestones.Content.Should().Contain("Alpha milestone");
    }

    private sealed class TeamOnlyAccessService(AuthorizedUserRecord user) : ICurrentAccessService
    {
        public Task<CurrentAccessSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CurrentAccessSnapshot(user.UserType, user));

        public Task<AccessLevel> GetAccessLevelAsync(
            string module,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(module == TeamHubModules.Team ? AccessLevel.ReadOnly : AccessLevel.NoAccess);

        public async Task<bool> CanAsync(
            string module,
            AccessLevel required = AccessLevel.ReadOnly,
            CancellationToken cancellationToken = default) =>
            (await GetAccessLevelAsync(module, cancellationToken)).Allows(required);
    }

    private sealed class CapturingChatService : IAiChatService
    {
        public AiChatUserContext? CapturedUser { get; private set; }

        public Task<IReadOnlyList<AiModelCatalogItem>> GetModelsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AiModelCatalogItem>>([]);

        public Task<AiChatResponse> SendAsync(
            AiChatUserContext user,
            AiChatRequest request,
            CancellationToken cancellationToken = default)
        {
            CapturedUser = user;
            return Task.FromResult(new AiChatResponse("Answer", request.Model, []));
        }
    }

    private sealed class ModuleAccessService(AuthorizedUserRecord user, string allowedModule) : ICurrentAccessService
    {
        public Task<CurrentAccessSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CurrentAccessSnapshot(user.UserType, user));

        public Task<AccessLevel> GetAccessLevelAsync(
            string module,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(module, allowedModule, StringComparison.Ordinal)
                ? AccessLevel.ReadOnly
                : AccessLevel.NoAccess);

        public async Task<bool> CanAsync(
            string module,
            AccessLevel required = AccessLevel.ReadOnly,
            CancellationToken cancellationToken = default) =>
            (await GetAccessLevelAsync(module, cancellationToken)).Allows(required);
    }

    private sealed class StubTeamDirectoryService(TeamDirectoryDto directory) : ITeamDirectoryService
    {
        public Task<TeamDirectoryDto> GetTeamDirectoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(directory);
    }

    private sealed class EmptyMilestoneService : IMilestoneTrackerService
    {
        public Task<IReadOnlyList<MilestoneDto>> GetMilestonesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MilestoneDto>>([]);

        public Task<MilestoneSourceTestResult> TestSourceAsync(
            MilestoneSourceSettings settings,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubMilestoneService : IMilestoneTrackerService
    {
        public Task<IReadOnlyList<MilestoneDto>> GetMilestonesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MilestoneDto>>(
            [
                new MilestoneDto
                {
                    SlNo = "1",
                    Program = "India Hero",
                    Title = "India Hero",
                    Milestone = "Alpha",
                    Description = "Alpha milestone"
                }
            ]);

        public Task<MilestoneSourceTestResult> TestSourceAsync(
            MilestoneSourceSettings settings,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class EmptyStudioService : IStudioDirectoryService
    {
        public Task<IReadOnlyList<StudioDetails>> GetStudiosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StudioDetails>>([]);

        public Task<StudioDetails?> GetStudioAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StudioDetails> SaveStudioAsync(
            StudioDetails studio,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteStudioAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubStudioService(params StudioDetails[] studios) : IStudioDirectoryService
    {
        public Task<IReadOnlyList<StudioDetails>> GetStudiosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StudioDetails>>(studios);

        public Task<StudioDetails?> GetStudioAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(studios.FirstOrDefault(item => item.Id == id));

        public Task<StudioDetails> SaveStudioAsync(
            StudioDetails studio,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteStudioAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubPlanningService : ISupportSummaryPlanningService
    {
        public Task<IReadOnlyList<ExpectedMilestoneDelivery>> GetExpectedDeliveriesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExpectedMilestoneDelivery>>(
                [new(Guid.NewGuid(), "Example Project", "Feature complete", new DateOnly(2026, 11, 6))]);

        public Task<IReadOnlyList<MilestoneBuildReview>> GetBuildReviewsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MilestoneBuildReview>>(
                [new(Guid.NewGuid(), "Example Project", "Feature complete", MilestoneBuildReviewStatuses.InReview,
                    new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 30))]);

        public Task<ExpectedMilestoneDelivery> SaveExpectedDeliveryAsync(
            ExpectedMilestoneDelivery delivery,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteExpectedDeliveryAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MilestoneBuildReview> SaveBuildReviewAsync(
            MilestoneBuildReview review,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteBuildReviewAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubJiraService : IStudioJiraTicketService
    {
        public Task<StudioJiraTicketResult> GetTicketsAsync(
            StudioJiraTicketQuery query,
            CancellationToken cancellationToken = default) => Task.FromResult(new StudioJiraTicketResult
        {
            IsConfigured = true,
            Groups =
            [
                new StudioJiraTicketGroup
                {
                    Name = "Support",
                    Tickets =
                    [
                        new StudioJiraTicket
                        {
                            TicketId = "TEAM-42",
                            Summary = "Fix the current support issue",
                            Status = "In Progress"
                        }
                    ]
                }
            ]
        });
    }

    private sealed class StubConfluenceService : IStudioConfluenceUpdateService
    {
        public Task<StudioConfluenceUpdateResult> GetConsolidatedUpdatesAsync(
            ConsolidatedStudioConfluenceUpdateQuery query,
            CancellationToken cancellationToken = default) => Task.FromResult(new StudioConfluenceUpdateResult
        {
            IsConfigured = true,
            Updates =
            [
                new StudioConfluenceWeeklyUpdate
                {
                    StudioId = "studio-1",
                    WeekStart = new DateOnly(2026, 10, 26),
                    WeekEnd = new DateOnly(2026, 10, 30),
                    PageTitle = "Weekly update",
                    StudioWork = "Current studio work"
                }
            ]
        });

        public Task<StudioConfluenceUpdateResult> GetUpdatesAsync(
            StudioConfluenceUpdateQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UpdateStudioConfluenceResult> UpdateAsync(
            UpdateStudioConfluenceCommand command,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingJiraService : IStudioJiraTicketService
    {
        public Task<StudioJiraTicketResult> GetTicketsAsync(
            StudioJiraTicketQuery query,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Jira must not be called without consent.");
    }

    private sealed class ThrowingConfluenceService : IStudioConfluenceUpdateService
    {
        public Task<StudioConfluenceUpdateResult> GetConsolidatedUpdatesAsync(
            ConsolidatedStudioConfluenceUpdateQuery query,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Confluence must not be called without consent.");

        public Task<StudioConfluenceUpdateResult> GetUpdatesAsync(
            StudioConfluenceUpdateQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UpdateStudioConfluenceResult> UpdateAsync(
            UpdateStudioConfluenceCommand command,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedEffortService : IWorklogEffortService
    {
        public Task<WorklogEffortReport> GetActualEffortAsync(
            DateOnly startDate,
            DateOnly endDate,
            string requestingUserId,
            bool allowPrivilegedDefaultCredential,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class EmptyReviewService : IMilestoneReviewService
    {
        public Task<Review?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Review?>(null);

        public Task<IReadOnlyList<Review>> GetActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Review>>([]);

        public Task<Review?> GetDraftAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Review?>(null);

        public Task<IReadOnlyList<Review>> GetHistoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Review>>([]);

        public Task<Review?> CreateDraftAsync(
            CreateMilestoneReviewRequest request,
            string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Review?> PublishDraftAsync(
            string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DiscardDraftAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UpdateAsync(
            Guid reviewId,
            CreateMilestoneReviewRequest request,
            Guid actorId,
            bool isAdmin,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ArchiveAsync(
            Guid reviewId,
            bool isAdmin,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(
            Guid reviewId,
            bool isAdmin,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RestoreFeedbackAsync(
            IReadOnlyList<RestoreMilestoneReviewFeedbackRequest> requests,
            bool isAdmin,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SaveFeedbackAsync(
            SaveMilestoneReviewFeedbackRequest request,
            Guid actorId,
            bool isAdmin,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class EmptyWorkflowService : IWorkflowReadService
    {
        public Task<DashboardSummaryDto> GetDashboardSummaryAsync(
            string? assignedTo = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<WorkflowSummaryDto>> GetWorkflowSummariesAsync(
            WorkflowInstanceStatus? status = null,
            int? limit = null,
            string? assignedTo = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkflowSummaryDto>>([]);

        public Task<IReadOnlyList<TaskItemDto>> GetMyTasksAsync(
            string owner,
            bool includeAllOwners = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
