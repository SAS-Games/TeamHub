using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Application.Interfaces;
using TeamHub.Application.Models;
using TeamHub.Authentication;
using TeamHub.Domain.Enums;

namespace TeamHub.Web.Pages.WorkCenter;

public class OverviewModel(IWorkflowReadService readService) : PageModel
{
    public DashboardSummaryDto Summary { get; private set; } = new();
    public IReadOnlyList<WorkflowSummaryDto> ActiveWorkflows { get; private set; } = [];
    public IReadOnlyList<WorkflowSummaryDto> CompletedWorkflows { get; private set; } = [];
    public IReadOnlyList<WorkflowSummaryDto> CancelledWorkflows { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var assignedTo = User.IsInRole(TeamHubUserTypes.Admin)
            || User.IsInRole(TeamHubUserTypes.Privileged)
                ? null
                : User.Identity?.Name ?? string.Empty;
        Summary = await readService.GetDashboardSummaryAsync(assignedTo, cancellationToken);
        ActiveWorkflows = await readService.GetWorkflowSummariesAsync(
            WorkflowInstanceStatus.InProgress,
            assignedTo: assignedTo,
            cancellationToken: cancellationToken);
        CompletedWorkflows = await readService.GetWorkflowSummariesAsync(
            WorkflowInstanceStatus.Completed,
            10,
            assignedTo,
            cancellationToken);
        CancelledWorkflows = await readService.GetWorkflowSummariesAsync(
            WorkflowInstanceStatus.Cancelled,
            10,
            assignedTo,
            cancellationToken);
    }
}
