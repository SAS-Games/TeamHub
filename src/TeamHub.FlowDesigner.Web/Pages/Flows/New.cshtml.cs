using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Web.Pages.Flows;

public sealed class NewModel(IFlowService flows) : PageModel
{
    [BindProperty]
    public string Name { get; set; } = string.Empty;

    [BindProperty]
    public string Description { get; set; } = string.Empty;

    [BindProperty]
    public DiagramType DiagramType { get; set; } = DiagramType.StandardFlowchart;

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            ModelState.AddModelError(nameof(Name), "Enter a name for the diagram.");
            return Page();
        }

        try
        {
            var flow = await flows.CreateAsync(Name, Description, DiagramType, cancellationToken: cancellationToken);
            return RedirectToPage("/Flows/Edit", new { id = flow.Id });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }
}
