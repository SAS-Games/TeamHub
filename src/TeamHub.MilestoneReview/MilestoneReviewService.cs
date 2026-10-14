using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace TeamHub.MilestoneReview;

public sealed partial class MilestoneReviewService(
    MilestoneReviewDbContext dbContext,
    IMilestoneReviewUserDirectory users,
    IMilestoneReviewCommentPublisher commentPublisher) : IMilestoneReviewService
{
    public async Task<MilestoneReview?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var review = await dbContext.Reviews.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Status == MilestoneReviewStatus.Published, cancellationToken);
        return review is null ? null : await ToDtoAsync(review, cancellationToken);
    }

    public async Task<MilestoneReview?> GetDraftAsync(CancellationToken cancellationToken = default)
    {
        var review = await dbContext.Reviews.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Status == MilestoneReviewStatus.Draft, cancellationToken);
        return review is null ? null : await ToDtoAsync(review, cancellationToken);
    }

    public async Task<IReadOnlyList<MilestoneReview>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        var rawReviews = await dbContext.Reviews.AsNoTracking()
            .Where(item => item.Status == MilestoneReviewStatus.Superseded)
            .ToListAsync(cancellationToken);

        if (rawReviews.Count == 0) return [];

        var reviews = rawReviews
            .OrderByDescending(item => item.PublishedAt)
            .ThenByDescending(item => item.CreatedAt)
            .ToList();

        var reviewIds = reviews.Select(item => item.Id).ToList();

        var assignments = await dbContext.Assignments.AsNoTracking()
            .Where(item => reviewIds.Contains(item.ReviewId))
            .OrderBy(item => item.Reviewer)
            .Select(item => new
            {
                item.ReviewId,
                Dto = new MilestoneReviewAssignedReviewer(item.ReviewerId, item.Reviewer, item.ReviewerUserId)
            })
            .ToListAsync(cancellationToken);
        var assignmentsByReviewId = assignments
            .GroupBy(item => item.ReviewId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<MilestoneReviewAssignedReviewer>)group.Select(x => x.Dto).ToList());

        var feedback = await dbContext.Feedback.AsNoTracking()
            .Where(item => reviewIds.Contains(item.ReviewId))
            .OrderBy(item => item.Reviewer)
            .Select(item => new
            {
                item.ReviewId,
                Dto = new MilestoneReviewFeedback(
                    item.Id, item.ReviewerId, item.Reviewer, item.SessionDetails, item.WhatGood,
                    item.NeedImprovements, item.AdditionalFeedback, item.Version, item.JiraCommentId, item.JiraSyncError)
            })
            .ToListAsync(cancellationToken);
        var feedbackByReviewId = feedback
            .GroupBy(item => item.ReviewId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<MilestoneReviewFeedback>)group.Select(x => x.Dto).ToList());

        return reviews.Select(review => new MilestoneReview(
            review.Id,
            review.ProjectName,
            review.MilestoneName,
            review.Description,
            review.Status,
            review.CreatedAt,
            review.PublishedAt,
            review.JiraTicketId,
            assignmentsByReviewId.GetValueOrDefault(review.Id, []),
            feedbackByReviewId.GetValueOrDefault(review.Id, []))).ToList();
    }

    public async Task<MilestoneReview?> CreateDraftAsync(
        CreateMilestoneReviewRequest request,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var details = ValidateDetails(request);
        if (await dbContext.Reviews.AnyAsync(item => item.Status == MilestoneReviewStatus.Draft, cancellationToken))
            throw new InvalidOperationException("A milestone review draft already exists. Publish or remove it before creating another.");

        var activeUsers = await users.GetActiveUsersAsync(cancellationToken);
        var byId = activeUsers.Where(user => user.IsActive && user.IsRegistered)
            .ToDictionary(user => user.Id);
        var reviewerIds = request.ReviewerIds.Distinct().ToList();
        if (reviewerIds.Any(id => !byId.ContainsKey(id)))
            throw new ArgumentException("The reviewer selection contains an inactive or unavailable user.");

        var review = new MilestoneReviewRecord
        {
            ProjectName = details.ProjectName,
            MilestoneName = details.MilestoneName,
            Description = details.Description,
            JiraTicketId = details.JiraTicketId,
            Status = MilestoneReviewStatus.Draft,
            CreatedBy = Required(actor, 320, "Creator"),
            CreatedAt = DateTimeOffset.UtcNow
        };
        dbContext.Reviews.Add(review);
        foreach (var reviewerId in reviewerIds)
        {
            dbContext.Assignments.Add(new MilestoneReviewAssignmentRecord
            {
                ReviewId = review.Id,
                ReviewerId = reviewerId,
                Reviewer = Required(byId[reviewerId].DisplayName, 160, "Reviewer display name"),
                ReviewerUserId = Required(byId[reviewerId].UserId, 320, "Reviewer email")
            });
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(review, cancellationToken);
    }

    public async Task<MilestoneReview?> PublishDraftAsync(string actor, CancellationToken cancellationToken = default)
    {
        _ = Required(actor, 320, "Publisher");
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var draft = await dbContext.Reviews.SingleOrDefaultAsync(
            item => item.Status == MilestoneReviewStatus.Draft, cancellationToken);
        if (draft is null) return null;

        var now = DateTimeOffset.UtcNow;
        await dbContext.Reviews.Where(item => item.Status == MilestoneReviewStatus.Published)
            .ExecuteUpdateAsync(update => update.SetProperty(
                item => item.Status, MilestoneReviewStatus.Superseded), cancellationToken);
        draft.Status = MilestoneReviewStatus.Published;
        draft.PublishedAt = now;
        var assignments = await dbContext.Assignments.AsNoTracking()
            .Where(item => item.ReviewId == draft.Id)
            .ToListAsync(cancellationToken);
        foreach (var assignment in assignments)
        {
            dbContext.Feedback.Add(new MilestoneReviewFeedbackRecord
            {
                ReviewId = draft.Id,
                ReviewerId = assignment.ReviewerId,
                Reviewer = assignment.Reviewer,
                ReviewerUserId = assignment.ReviewerUserId,
                UpdatedAt = now
            });
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ToDtoAsync(draft, cancellationToken);
    }

    public async Task DiscardDraftAsync(CancellationToken cancellationToken = default)
    {
        var draft = await dbContext.Reviews.SingleOrDefaultAsync(
            item => item.Status == MilestoneReviewStatus.Draft, cancellationToken);
        if (draft is null) return;
        await dbContext.Assignments.Where(item => item.ReviewId == draft.Id)
            .ExecuteDeleteAsync(cancellationToken);
        dbContext.Reviews.Remove(draft);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ArchiveAsync(Guid reviewId, bool isAdmin, CancellationToken cancellationToken = default)
    {
        if (!isAdmin)
            throw new UnauthorizedAccessException("Only administrators can archive milestone reviews.");
        var updated = await dbContext.Reviews
            .Where(item => item.Id == reviewId && item.Status == MilestoneReviewStatus.Published)
            .ExecuteUpdateAsync(update => update.SetProperty(
                item => item.Status, MilestoneReviewStatus.Superseded), cancellationToken);
        if (updated == 0)
            throw new InvalidOperationException("The published review was not found or has already been archived. Reload the page.");
    }

    public async Task UpdateAsync(Guid reviewId, CreateMilestoneReviewRequest request, bool isAdmin,
        CancellationToken cancellationToken = default)
    {
        if (!isAdmin)
            throw new UnauthorizedAccessException("Only administrators can edit milestone review details.");
        var details = ValidateDetails(request);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var review = await dbContext.Reviews.SingleOrDefaultAsync(
            item => item.Id == reviewId && (item.Status == MilestoneReviewStatus.Draft
                || item.Status == MilestoneReviewStatus.Published), cancellationToken)
            ?? throw new InvalidOperationException("The draft or published review was not found. Archived reviews cannot be edited.");
        var assignments = await dbContext.Assignments.Where(item => item.ReviewId == reviewId)
            .ToListAsync(cancellationToken);
        var existingIds = assignments.Select(item => item.ReviewerId).ToHashSet();
        var addedIds = details.ReviewerIds.Where(id => !existingIds.Contains(id)).ToList();
        var activeUsers = await users.GetActiveUsersAsync(cancellationToken);
        var byId = activeUsers.Where(user => user.IsActive && user.IsRegistered).ToDictionary(user => user.Id);
        if (addedIds.Any(id => !byId.ContainsKey(id)))
            throw new ArgumentException("The reviewer selection contains an inactive or unavailable user.");

        var feedback = await dbContext.Feedback.Where(item => item.ReviewId == reviewId)
            .ToListAsync(cancellationToken);
        if (!string.Equals(review.JiraTicketId, details.JiraTicketId, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var row in feedback)
            {
                row.JiraCommentId = null;
                row.JiraSyncError = null;
                row.Version++;
            }
        }
        review.ProjectName = details.ProjectName;
        review.MilestoneName = details.MilestoneName;
        review.Description = details.Description;
        review.JiraTicketId = details.JiraTicketId;
        dbContext.Assignments.RemoveRange(assignments.Where(item => !details.ReviewerIds.Contains(item.ReviewerId)));
        foreach (var id in addedIds)
        {
            var user = byId[id];
            var displayName = Required(user.DisplayName, 160, "Reviewer display name");
            var email = Required(user.UserId, 320, "Reviewer email");
            dbContext.Assignments.Add(new MilestoneReviewAssignmentRecord
            {
                ReviewId = reviewId, ReviewerId = id, Reviewer = displayName, ReviewerUserId = email
            });
            if (review.Status == MilestoneReviewStatus.Published && feedback.All(item => item.ReviewerId != id))
                dbContext.Feedback.Add(new MilestoneReviewFeedbackRecord
                {
                    ReviewId = reviewId, ReviewerId = id, Reviewer = displayName,
                    ReviewerUserId = email, UpdatedAt = DateTimeOffset.UtcNow
                });
        }
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new InvalidOperationException("Feedback changed while editing this review. Reload the page before saving again.", exception);
        }
    }

    public async Task DeleteAsync(Guid reviewId, bool isAdmin, CancellationToken cancellationToken = default)
    {
        if (!isAdmin)
            throw new UnauthorizedAccessException("Only administrators can delete milestone reviews.");
        var deleted = await dbContext.Reviews
            .Where(item => item.Id == reviewId
                && (item.Status == MilestoneReviewStatus.Published || item.Status == MilestoneReviewStatus.Superseded))
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
            throw new InvalidOperationException("The published or archived review was not found. Reload the page.");
    }

    public async Task SaveFeedbackAsync(
        SaveMilestoneReviewFeedbackRequest request,
        Guid actorId,
        bool isAdmin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (actorId == Guid.Empty)
            throw new UnauthorizedAccessException("An authenticated reviewer is required.");
        var isPublished = await dbContext.Reviews.AnyAsync(
            item => item.Id == request.ReviewId && item.Status == MilestoneReviewStatus.Published,
            cancellationToken);
        if (!isPublished) throw new InvalidOperationException("Feedback can only be changed on the current published review.");

        var feedback = await dbContext.Feedback.SingleOrDefaultAsync(
            item => item.Id == request.FeedbackId && item.ReviewId == request.ReviewId,
            cancellationToken) ?? throw new KeyNotFoundException("The feedback row was not found.");
        if (!isAdmin && (feedback.ReviewerId != actorId
            || !await dbContext.Assignments.AnyAsync(
                item => item.ReviewId == request.ReviewId && item.ReviewerId == actorId, cancellationToken)))
            throw new UnauthorizedAccessException("You can only update your own assigned feedback row.");
        if (feedback.Version != request.Version)
            throw new InvalidOperationException("This feedback row was changed by another user. Reload the page before saving again.");

        var sessionDetails = Bounded(request.SessionDetails, 20000, "Session Details");
        var whatGood = Bounded(request.WhatGood, 20000, "What's Good");
        var needImprovements = Bounded(request.NeedImprovements, 20000, "Needs Improvement");
        var additionalFeedback = Bounded(request.AdditionalFeedback, 20000, "Additional Feedback");
        feedback.SessionDetails = sessionDetails;
        feedback.WhatGood = whatGood;
        feedback.NeedImprovements = needImprovements;
        feedback.AdditionalFeedback = additionalFeedback;
        feedback.Version++;
        feedback.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new InvalidOperationException(
                "This feedback row was changed by another user. Reload the page before saving again.", exception);
        }
        var review = await dbContext.Reviews.AsNoTracking()
            .SingleAsync(item => item.Id == feedback.ReviewId, cancellationToken);
        if (string.IsNullOrWhiteSpace(review.JiraTicketId)) return;
        try
        {
            var result = await commentPublisher.UpsertAsync(new MilestoneReviewCommentRequest(
                review.JiraTicketId,
                feedback.ReviewerUserId,
                feedback.Id,
                feedback.JiraCommentId,
                review.ProjectName,
                review.MilestoneName,
                feedback.Reviewer,
                feedback.SessionDetails,
                feedback.WhatGood,
                feedback.NeedImprovements,
                feedback.AdditionalFeedback), cancellationToken);
            feedback.JiraCommentId = result.CommentId;
            feedback.JiraSyncError = null;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (MilestoneReviewCommentSyncException exception)
        {
            feedback.JiraSyncError = exception.Message;
            await dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or System.Text.Json.JsonException or InvalidOperationException)
        {
            var syncException = new MilestoneReviewCommentSyncException(
                "Feedback was saved in Team Hub, but Jira could not be updated. Save again to retry synchronization.",
                exception);
            feedback.JiraSyncError = syncException.Message;
            await dbContext.SaveChangesAsync(cancellationToken);
            throw syncException;
        }
    }

    private async Task<MilestoneReview> ToDtoAsync(MilestoneReviewRecord review, CancellationToken cancellationToken)
    {
        var assignments = await dbContext.Assignments.AsNoTracking()
            .Where(item => item.ReviewId == review.Id)
            .OrderBy(item => item.Reviewer)
            .Select(item => new MilestoneReviewAssignedReviewer(item.ReviewerId, item.Reviewer, item.ReviewerUserId))
            .ToListAsync(cancellationToken);
        var feedback = await dbContext.Feedback.AsNoTracking()
            .Where(item => item.ReviewId == review.Id)
            .OrderBy(item => item.Reviewer)
            .Select(item => new MilestoneReviewFeedback(
                item.Id, item.ReviewerId, item.Reviewer, item.SessionDetails, item.WhatGood,
                item.NeedImprovements, item.AdditionalFeedback, item.Version, item.JiraCommentId, item.JiraSyncError))
            .ToListAsync(cancellationToken);
        return new MilestoneReview(
            review.Id, review.ProjectName, review.MilestoneName, review.Description,
            review.Status, review.CreatedAt, review.PublishedAt, review.JiraTicketId, assignments, feedback);
    }

    private static CreateMilestoneReviewRequest ValidateDetails(CreateMilestoneReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ReviewerIds);
        var project = Required(request.ProjectName, 160, "Project name");
        var milestone = Required(request.MilestoneName, 160, "Milestone");
        var description = Bounded(request.Description, 4000, "Description");
        var ticket = Bounded(request.JiraTicketId, 64, "Jira ticket ID");
        if (!string.IsNullOrWhiteSpace(ticket) && !JiraTicketKey().IsMatch(ticket))
            throw new ArgumentException("Jira ticket ID must use the project-key format, such as GAME-123.");
        if (request.ReviewerIds.Count == 0)
            throw new ArgumentException("Select at least one user who can provide feedback.");
        if (request.ReviewerIds.Distinct().Count() != request.ReviewerIds.Count)
            throw new ArgumentException("The reviewer selection contains an inactive or unavailable user.");
        return new(project, milestone, description, string.IsNullOrWhiteSpace(ticket) ? null : ticket, request.ReviewerIds);
    }

    private static string Required(string? value, int maxLength, string label)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maxLength)
            throw new ArgumentException($"{label} is required and must be no more than {maxLength} characters.");
        return normalized;
    }

    private static string Bounded(string? value, int maxLength, string label)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > maxLength)
            throw new ArgumentException($"{label} must be no more than {maxLength} characters.");
        return normalized;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*-[1-9][0-9]*$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex JiraTicketKey();
}
