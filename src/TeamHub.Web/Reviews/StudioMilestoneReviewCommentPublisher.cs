using TeamHub.MilestoneReview;
using TeamHub.Studio;

namespace TeamHub.Web.Reviews;

public sealed class StudioMilestoneReviewCommentPublisher(IStudioJiraCommentService jira)
    : IMilestoneReviewCommentPublisher
{
    public async Task<MilestoneReviewCommentResult> UpsertAsync(
        MilestoneReviewCommentRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await jira.UpsertMilestoneReviewCommentAsync(
                new StudioJiraMilestoneReviewCommentRequest(
                    request.TicketId,
                    request.RequestingUserId,
                    request.FeedbackId,
                    request.CommentId,
                    request.ProjectName,
                    request.MilestoneName,
                    request.Reviewer,
                    request.WhatGood,
                    request.WhatsBad,
                    request.NeedImprovements),
                cancellationToken);
            return new MilestoneReviewCommentResult(result.CommentId);
        }
        catch (StudioJiraCommentException exception)
        {
            throw new MilestoneReviewCommentSyncException(exception.Message, exception);
        }
    }
}
