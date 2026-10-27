using System.Text.Json;
using TeamHub.AI.Chat.Contracts;
using TeamHub.Application.Interfaces;
using TeamHub.Authentication;
using TeamHub.Domain.Enums;
using TeamHub.Milestones;
using TeamHub.MilestoneReview;
using TeamHub.Studio;
using TeamHub.Team;
using TeamHub.Web.AccessControl;

namespace TeamHub.Web.AI;

/// <summary>
/// TeamHub-specific adapter for the reusable AI Chat module. Authorization is
/// applied here, before any data can enter an AI prompt.
/// </summary>
public sealed class TeamHubChatKnowledgeSource(
    ICurrentAccessService access,
    IMilestoneTrackerService milestones,
    IStudioDirectoryService studios,
    IMilestoneReviewService reviews,
    IWorkflowReadService workflows,
    ITeamDirectoryService teamDirectory) : IAiChatKnowledgeSource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<IReadOnlyList<AiChatSource>> SearchAsync(
        AiChatUserContext user,
        string question,
        CancellationToken cancellationToken = default)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current.User is null
            || !string.Equals(current.User.UserId, user.UserId, StringComparison.OrdinalIgnoreCase))
            throw new AiChatException("The signed-in user could not be verified.");

        var result = new List<AiChatSource>();
        if (await access.CanAsync(TeamHubModules.Team, AccessLevel.ReadOnly, cancellationToken))
            await AddTeamDirectoryAsync(result, question, cancellationToken);
        if (await access.CanAsync(TeamHubModules.Milestones, AccessLevel.ReadOnly, cancellationToken))
            await AddMilestonesAsync(result, question, cancellationToken);
        if (await access.CanAsync(TeamHubModules.StudioConfiguration, AccessLevel.ReadOnly, cancellationToken))
            await AddProjectsAsync(result, question, cancellationToken);
        if (await access.CanAsync(TeamHubModules.MilestoneReview, AccessLevel.ReadOnly, cancellationToken))
            await AddReviewsAsync(result, question, cancellationToken);
        if (await access.CanAsync(TeamHubModules.WorkCenter, AccessLevel.ReadOnly, cancellationToken))
            await AddWorkflowsAsync(result, user.UserId, question, cancellationToken);
        return result;
    }

    private async Task AddTeamDirectoryAsync(
        ICollection<AiChatSource> sources,
        string question,
        CancellationToken cancellationToken)
    {
        var directory = await teamDirectory.GetTeamDirectoryAsync(cancellationToken);
        var members = SelectRelevant(directory.Members, question,
                item => $"team directory employee management {item.EmployeeName} {item.Role} {item.Gid} {item.Email} {item.ContactNumber} {item.Section}",
                100)
            .Select(item => new
            {
                item.EmployeeName,
                item.Role,
                item.Gid,
                item.Email,
                item.ContactNumber,
                Section = TeamMemberSections.Normalize(item.Section)
            });
        AddJsonSource(sources, "teamhub-team-directory", "TeamHub team directory", members);

        var specializations = SelectRelevant(directory.Specializations, question,
                item => $"team specialization responsibility matrix pod {item.Pod} {item.FocusAreas} {item.Members}", 50)
            .Select(item => new { item.Pod, item.FocusAreas, item.Members });
        AddJsonSource(sources, "teamhub-team-specializations", "TeamHub team specializations", specializations);
    }

    private async Task AddMilestonesAsync(
        ICollection<AiChatSource> sources,
        string question,
        CancellationToken cancellationToken)
    {
        var items = SelectRelevant(await milestones.GetMilestonesAsync(cancellationToken), question,
            item => $"milestone milestones tracker {item.Program} {item.Title} {item.Developer} {item.Milestone} {item.Description}", 100)
            .Select(item => new
            {
                item.SlNo,
                item.Program,
                item.Title,
                item.Developer,
                item.Milestone,
                item.Description,
                item.DeliveryDate,
                item.MsApprovalDate,
                item.ReleaseDate
            });
        AddJsonSource(sources, "teamhub-milestones", "TeamHub milestone tracker", items);
    }

    private async Task AddProjectsAsync(
        ICollection<AiChatSource> sources,
        string question,
        CancellationToken cancellationToken)
    {
        var items = SelectRelevant(
                (await studios.GetStudiosAsync(cancellationToken)).Where(item => item.IsActive),
                question,
                item => $"studio studios project projects {item.StudioName} {item.ProjectName} {item.StudioGroup} {item.Location}",
                60)
            .Select(item => new
            {
                item.StudioName,
                item.ProjectName,
                item.StudioGroup,
                item.Location,
                item.TimeZoneId,
                OurContacts = item.OurContacts.Select(contact => new
                {
                    contact.Name,
                    contact.Role
                }),
                TeamMembers = item.TeamMembers.Select(member => new
                {
                    member.Name,
                    member.RolesAndResponsibilities,
                    member.EmailId
                }),
                DevelopmentTools = item.DevelopmentTools.Select(tool => new { tool.Name, tool.Description }),
                ImportantLinks = item.ImportantLinks.Select(link => new { link.Label, link.Url, link.Description })
            });
        AddJsonSource(sources, "teamhub-projects", "TeamHub projects and studios", items);
    }

    private async Task AddReviewsAsync(
        ICollection<AiChatSource> sources,
        string question,
        CancellationToken cancellationToken)
    {
        var items = SelectRelevant(await reviews.GetActiveAsync(cancellationToken), question,
            item => $"milestone review reviews {item.ProjectName} {item.MilestoneName} {item.Description}", 50)
            .Select(item => new
            {
                item.Id,
                item.ProjectName,
                item.MilestoneName,
                item.Description,
                Status = item.Status.ToString(),
                item.PublishedAt,
                item.JiraTicketId,
                AssignedReviewers = item.AssignedReviewers.Select(reviewer => new
                {
                    reviewer.DisplayName,
                    reviewer.UserId
                }),
                ReportGenerators = item.ReportGenerators.Select(generator => new
                {
                    generator.DisplayName,
                    generator.UserId
                }),
                Feedback = item.Feedback.Select(feedback => new
                {
                    feedback.Reviewer,
                    SessionDetails = Clip(feedback.SessionDetails),
                    WhatGood = Clip(feedback.WhatGood),
                    NeedImprovements = Clip(feedback.NeedImprovements),
                    AdditionalFeedback = Clip(feedback.AdditionalFeedback)
                })
            });
        AddJsonSource(sources, "teamhub-milestone-reviews", "Published milestone reviews", items);
    }

    private async Task AddWorkflowsAsync(
        ICollection<AiChatSource> sources,
        string userId,
        string question,
        CancellationToken cancellationToken)
    {
        // Always scope to the signed-in assignee, including privileged users and
        // administrators. AI Chat intentionally uses a least-privilege view.
        var all = new List<object>();
        foreach (var status in new[]
                 {
                     WorkflowInstanceStatus.InProgress,
                     WorkflowInstanceStatus.Completed,
                     WorkflowInstanceStatus.Cancelled
                 })
        {
            var visible = await workflows.GetWorkflowSummariesAsync(
                status, 50, userId, cancellationToken);
            all.AddRange(SelectRelevant(visible, question,
                item => $"work center workflow workflows task tasks {item.InstanceName} {item.WorkflowName} {item.ActiveOwners} {item.ActiveSteps}", 30)
                .Select(item => new
                {
                    item.Id,
                    item.InstanceName,
                    item.WorkflowName,
                    Status = item.Status.ToString(),
                    item.StartedAtUtc,
                    item.CompletedAtUtc,
                    item.CancelledAtUtc,
                    item.NextDueAtUtc,
                    item.ProgressPercent,
                    item.OverdueCount,
                    item.ActiveOwners,
                    item.ActiveSteps
                }));
        }
        AddJsonSource(sources, "teamhub-my-workflows", "Workflows assigned to the signed-in user", all);

        var tasks = SelectRelevant(
                await workflows.GetMyTasksAsync(userId, includeAllOwners: false, cancellationToken),
                question,
                item => $"work center task tasks {item.InstanceName} {item.WorkflowName} {item.StepName} {item.Owner}",
                100)
            .Select(item => new
            {
                item.WorkflowInstanceId,
                item.InstanceName,
                item.WorkflowName,
                item.StepName,
                item.Owner,
                item.DueAtUtc,
                item.IsOverdue,
                item.CompletedAssignees,
                item.TotalAssignees
            });
        AddJsonSource(sources, "teamhub-my-tasks", "Tasks assigned to the signed-in user", tasks);
    }

    private static void AddJsonSource(
        ICollection<AiChatSource> sources,
        string id,
        string title,
        object value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        if (json != "[]") sources.Add(new AiChatSource(id, title, json));
    }

    private static IEnumerable<T> SelectRelevant<T>(
        IEnumerable<T> values,
        string question,
        Func<T, string> searchableText,
        int limit)
    {
        var tokens = question.Split(
                [' ', '\t', '\r', '\n', ',', '.', ':', ';', '/', '\\', '-', '_', '?', '!', '(', ')'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();

        if (tokens.Length == 0) return [];

        return values.Select(value => new
            {
                Value = value,
                Score = tokens.Count(token => searchableText(value).Contains(token, StringComparison.OrdinalIgnoreCase))
            })
            .OrderByDescending(item => item.Score)
            .Where(item => item.Score > 0)
            .Take(limit)
            .Select(item => item.Value);
    }

    private static string Clip(string? value, int maximumCharacters = 2_000)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters] + "…";
    }
}
