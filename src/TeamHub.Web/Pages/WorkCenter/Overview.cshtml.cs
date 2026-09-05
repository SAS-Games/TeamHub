using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Application.Interfaces;
using TeamHub.Application.Models;

namespace TeamHub.Web.Pages.WorkCenter;

public class OverviewModel(IWorkflowReadService readService) : PageModel
{
    public DashboardSummaryDto Summary { get; private set; } = new();
    public IReadOnlyList<WorkflowSummaryDto> Workflows { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Summary = await readService.GetDashboardSummaryAsync();
        Workflows = await readService.GetWorkflowSummariesAsync();
    }
}
