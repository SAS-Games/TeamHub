using TeamHub.Application.Interfaces;
using TeamHub.Application.Models;
using TeamHub.MilestoneReview;

namespace TeamHub.Web.Reviews;

public sealed class TeamHubMilestoneReviewWorkflowStarter(IWorkflowEngine workflows)
    : IMilestoneReviewWorkflowStarter
{
    public Task<Guid> StartAsync(
        MilestoneReviewWorkflowStartRequest request,
        CancellationToken cancellationToken = default) =>
        workflows.StartWorkflowAsync(new StartWorkflowRequest
        {
            WorkflowKey = request.WorkflowKey,
            InstanceName = request.InstanceName,
            Description = request.Description,
            StartedBy = request.StartedBy,
            MetadataJson = request.MetadataJson
        }, cancellationToken);
}
