using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Web.Pages.Flows;

public sealed class EditModel(
    IFlowService flows,
    IFlowPublicationService publicationService,
    IFlowPermissionService permissions) : PageModel
{
    public Guid FlowId { get; private set; }
    public string FlowName { get; private set; } = string.Empty;
    public DiagramType DiagramType { get; private set; }
    public bool CanPublish { get; private set; }
    public bool CanSaveAsTemplate { get; private set; }
    public string BackPath { get; private set; } = "/flows";

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var flow = await flows.GetAsync(id, cancellationToken);
        if (flow is null)
        {
            return NotFound();
        }

        FlowId = flow.Id;
        FlowName = flow.Name;
        DiagramType = flow.DiagramType;
        CanPublish = publicationService.CanPublish(flow);
        CanSaveAsTemplate = permissions.CanManageTemplates();
        BackPath = flow.DiagramType == DiagramType.WorkCenterWorkflow
            ? Url.Page("/WorkCenter/Configuration") ?? "/WorkCenter/Configuration"
            : Url.Page("/Flows/Index") ?? "/flows";
        return Page();
    }
}
