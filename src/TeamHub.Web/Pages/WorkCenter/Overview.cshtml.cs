using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Application.Interfaces;
using TeamHub.Application.Models;
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
        Summary = await readService.GetDashboardSummaryAsync(cancellationToken);
        ActiveWorkflows = await readService.GetWorkflowSummariesAsync(WorkflowInstanceStatus.InProgress, cancellationToken: cancellationToken);
        CompletedWorkflows = await readService.GetWorkflowSummariesAsync(WorkflowInstanceStatus.Completed, 10, cancellationToken);
        CancelledWorkflows = await readService.GetWorkflowSummariesAsync(WorkflowInstanceStatus.Cancelled, 10, cancellationToken);
    }
}
