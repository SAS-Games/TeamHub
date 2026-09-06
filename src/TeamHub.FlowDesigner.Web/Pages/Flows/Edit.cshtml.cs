using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.FlowDesigner.Core.Contracts;

namespace TeamHub.FlowDesigner.Web.Pages.Flows;

public sealed class EditModel(IFlowService flows) : PageModel
{
    public Guid FlowId { get; private set; }
    public string FlowName { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var flow = await flows.GetAsync(id, cancellationToken);
        if (flow is null)
        {
            return NotFound();
        }

        FlowId = flow.Id;
        FlowName = flow.Name;
        return Page();
    }
}
