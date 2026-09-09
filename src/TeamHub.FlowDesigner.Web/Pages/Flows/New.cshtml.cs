using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Web.Pages.Flows;

public sealed class NewModel(
    IFlowService flows,
    IFlowTemplateCatalogService templates,
    IFlowPermissionService permissions) : PageModel
{
    public bool CanCreateWorkCenterWorkflow => permissions.CanUseDiagramType(DiagramType.WorkCenterWorkflow);
    public IReadOnlyList<TemplateCatalogSummary> Templates { get; private set; } = [];

    [BindProperty]
    public string Name { get; set; } = string.Empty;

    [BindProperty]
    public string Description { get; set; } = string.Empty;

    [BindProperty]
    public DiagramType DiagramType { get; set; } = DiagramType.StandardFlowchart;

    [BindProperty]
    public Guid? TemplateId { get; set; }

    public async Task OnGetAsync(Guid? templateId = null, CancellationToken cancellationToken = default)
    {
        TemplateId = templateId;
        await LoadTemplatesAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            ModelState.AddModelError(nameof(Name), "Enter a name for the diagram.");
            await LoadTemplatesAsync(cancellationToken);
            return Page();
        }

        try
        {
            var flow = TemplateId.HasValue
                ? await templates.CreateFlowAsync(TemplateId.Value, Name, cancellationToken)
                : await flows.CreateAsync(Name, Description, DiagramType, FlowTemplate.Blank, cancellationToken);
            return RedirectToPage("/Flows/Edit", new { id = flow.Id });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    private async Task LoadTemplatesAsync(CancellationToken cancellationToken)
    {
        Templates = await templates.ListAsync(cancellationToken);
    }
}
