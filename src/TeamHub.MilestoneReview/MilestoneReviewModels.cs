namespace TeamHub.MilestoneReview;

public enum MilestoneReviewStatus
{
    Draft,
    Published,
    Superseded
}

public sealed record MilestoneReviewUser(Guid Id, string DisplayName, string UserId, bool IsActive, bool IsRegistered);

public sealed record MilestoneReviewFeedback(
    Guid Id,
    Guid ReviewerId,
    string Reviewer,
    string SessionDetails,
    string WhatGood,
    string NeedImprovements,
    string AdditionalFeedback,
    int Version,
    string? JiraCommentId,
    string? JiraSyncError);

public sealed record MilestoneReviewAssignedReviewer(Guid Id, string DisplayName, string UserId);

public sealed record MilestoneReview(
    Guid Id,
    string ProjectName,
    string MilestoneName,
    string Description,
    MilestoneReviewStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    string? JiraTicketId,
    IReadOnlyList<MilestoneReviewAssignedReviewer> AssignedReviewers,
    IReadOnlyList<MilestoneReviewFeedback> Feedback,
    string? WorkflowKey,
    Guid? WorkflowInstanceId);

public sealed record CreateMilestoneReviewRequest(
    string ProjectName,
    string MilestoneName,
    string Description,
    string? JiraTicketId,
    IReadOnlyCollection<Guid> ReviewerIds,
    string? WorkflowKey = null);

public sealed record MilestoneReviewWorkflowStartRequest(
    Guid ReviewId,
    string WorkflowKey,
    string InstanceName,
    string Description,
    string StartedBy,
    string MetadataJson);

public interface IMilestoneReviewWorkflowStarter
{
    Task<Guid> StartAsync(
        MilestoneReviewWorkflowStartRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record SaveMilestoneReviewFeedbackRequest(
    Guid ReviewId,
    Guid FeedbackId,
    int Version,
    string SessionDetails,
    string WhatGood,
    string NeedImprovements,
    string AdditionalFeedback);

public sealed record RestoreMilestoneReviewFeedbackRequest(
    Guid ReviewId, string TicketId, Guid FeedbackId, int Version, string CommentId,
    string SessionDetails, string WhatGood, string NeedImprovements, string AdditionalFeedback);

public interface IMilestoneReviewUserDirectory
{
    Task<IReadOnlyList<MilestoneReviewUser>> GetActiveUsersAsync(CancellationToken cancellationToken = default);
}

public sealed record MilestoneReviewCommentRequest(
    string TicketId,
    string RequestingUserId,
    Guid FeedbackId,
    string? CommentId,
    string ProjectName,
    string MilestoneName,
    string Reviewer,
    string SessionDetails,
    string WhatGood,
    string NeedImprovements,
    string AdditionalFeedback);

public sealed record MilestoneReviewCommentResult(string CommentId);

public interface IMilestoneReviewCommentPublisher
{
    Task<MilestoneReviewCommentResult> UpsertAsync(
        MilestoneReviewCommentRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class MilestoneReviewCommentSyncException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public interface IMilestoneReviewService
{
    Task<MilestoneReview?> GetCurrentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MilestoneReview>> GetActiveAsync(CancellationToken cancellationToken = default);
    Task<MilestoneReview?> GetDraftAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MilestoneReview>> GetHistoryAsync(CancellationToken cancellationToken = default);
    Task<MilestoneReview?> CreateDraftAsync(
        CreateMilestoneReviewRequest request,
        string actor,
        CancellationToken cancellationToken = default);
    Task<MilestoneReview?> PublishDraftAsync(string actor, CancellationToken cancellationToken = default);
    Task DiscardDraftAsync(CancellationToken cancellationToken = default);
    Task UpdateAsync(
        Guid reviewId,
        CreateMilestoneReviewRequest request,
        bool isAdmin,
        CancellationToken cancellationToken = default);
    Task ArchiveAsync(Guid reviewId, bool isAdmin, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid reviewId, bool isAdmin, CancellationToken cancellationToken = default);
    Task RestoreFeedbackAsync(
        IReadOnlyList<RestoreMilestoneReviewFeedbackRequest> requests,
        bool isAdmin,
        CancellationToken cancellationToken = default);
    Task SaveFeedbackAsync(
        SaveMilestoneReviewFeedbackRequest request,
        Guid actorId,
        bool isAdmin,
        CancellationToken cancellationToken = default);
}
