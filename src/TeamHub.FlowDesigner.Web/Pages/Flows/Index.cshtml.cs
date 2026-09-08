using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Web.Pages.Flows;

public sealed class IndexModel(IFlowService flows) : PageModel
{
    public IReadOnlyList<FlowSummary> Flows { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken) => Flows = await flows.ListAsync(cancellationToken);

    public async Task<IActionResult> OnPostCreateAsync(string name, string? description, CancellationToken cancellationToken)
    {
        try
        {
            var flow = await flows.CreateAsync(name, description, cancellationToken: cancellationToken);
            return RedirectToPage("/Flows/Edit", new { id = flow.Id });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    public async Task<IActionResult> OnPostCreateExampleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var flow = await flows.CreateAsync(
                "Integration QA workflow",
                diagramType: DiagramType.StandardFlowchart,
                template: FlowTemplate.IntegrationQa,
                cancellationToken: cancellationToken);
            return RedirectToPage("/Flows/Edit", new { id = flow.Id });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    public async Task<IActionResult> OnPostRenameAsync(Guid id, string name, CancellationToken cancellationToken)
    {
        try
        {
            await flows.RenameAsync(id, name, cancellationToken);
            return RedirectToPage();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    public async Task<IActionResult> OnPostDuplicateAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var copy = await flows.DuplicateAsync(id, cancellationToken);
            return RedirectToPage("/Flows/Edit", new { id = copy.Id });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await flows.DeleteAsync(id, cancellationToken);
            return RedirectToPage();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }
}
