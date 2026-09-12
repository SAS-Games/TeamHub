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
    public bool CanEdit { get; private set; }
    public bool IsShared { get; private set; }
    public bool CanPublish { get; private set; }
    public bool CanSaveAsTemplate { get; private set; }
    public IReadOnlyList<FlowBreadcrumb> Breadcrumbs { get; private set; } = [];
    public string Trail { get; private set; } = string.Empty;
    public string BackPath { get; private set; } = "/flows";

    public async Task<IActionResult> OnGetAsync(Guid id, string? trail, CancellationToken cancellationToken)
    {
        var flow = await flows.GetAsync(id, cancellationToken);
        if (flow is null)
        {
            return NotFound();
        }

        FlowId = flow.Id;
        FlowName = flow.Name;
        DiagramType = flow.DiagramType;
        CanEdit = permissions.CanEdit(flow.CreatedBy);
        IsShared = flow.IsShared;
        CanPublish = publicationService.CanPublish(flow);
        CanSaveAsTemplate = permissions.CanManageTemplates();
        await LoadBreadcrumbsAsync(id, trail, cancellationToken);
        BackPath = Breadcrumbs.Count > 0
            ? Breadcrumbs[^1].Url
            : flow.DiagramType == DiagramType.WorkCenterWorkflow
                ? Url.Page("/WorkCenter/Configuration") ?? "/WorkCenter/Configuration"
                : Url.Page("/Flows/Index") ?? "/flows";
        return Page();
    }

    private async Task LoadBreadcrumbsAsync(Guid currentId, string? trail, CancellationToken cancellationToken)
    {
        var breadcrumbs = new List<FlowBreadcrumb>();
        var prefix = new List<Guid>();
        var seen = new HashSet<Guid> { currentId };
        var ids = (trail ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Take(20);

        foreach (var id in ids)
        {
            if (!seen.Add(id)) break;
            var ancestor = await flows.GetAsync(id, cancellationToken);
            if (ancestor is null) break;
            var ancestorTrail = string.Join(',', prefix);
            var url = Url.Page("/Flows/Edit", new { id, trail = ancestorTrail }) ?? $"/flows/{id}/edit";
            breadcrumbs.Add(new FlowBreadcrumb(id, ancestor.Name, url));
            prefix.Add(id);
        }

        Breadcrumbs = breadcrumbs;
        Trail = string.Join(',', breadcrumbs.Select(item => item.Id));
    }
}

public sealed record FlowBreadcrumb(Guid Id, string Name, string Url);
