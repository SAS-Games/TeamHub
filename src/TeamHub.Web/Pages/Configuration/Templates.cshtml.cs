using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.Web.Pages.Configuration;

[Authorize(Roles = "Admin")]
public sealed class TemplatesModel(IFlowTemplateCatalogService templates) : PageModel
{
    public IReadOnlyList<TemplateCatalogSummary> Templates { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Templates = await templates.ListAsync(cancellationToken);
    }
}
