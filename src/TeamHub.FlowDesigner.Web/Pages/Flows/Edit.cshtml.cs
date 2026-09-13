using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Web.Pages.Flows;

public sealed class EditModel(
    IFlowService flows,
    IFlowPublicationWorkflowService publications,
    IFlowPermissionService permissions) : PageModel
{
    public Guid FlowId { get; private set; }
    public string FlowName { get; private set; } = string.Empty;
    public DiagramType DiagramType { get; private set; }
    public bool CanEdit { get; private set; }
    public bool IsShared { get; private set; }
    public bool CanRequestPublication { get; private set; }
    public bool HasPendingPublicationRequest { get; private set; }
    public FlowPublicationRequestStatus? LatestPublicationStatus { get; private set; }
    public string? PublicationReviewNote { get; private set; }
    public bool CanSaveAsTemplate { get; private set; }
    public bool IsPublishedView { get; private set; }
    public bool IsReviewPreview { get; private set; }
    public bool StartInPresentation { get; private set; }
    public string LoadUrl { get; private set; } = string.Empty;
    public string SaveUrl { get; private set; } = string.Empty;
    public string LinkTargetsUrl { get; private set; } = string.Empty;
    public Guid? ReviewRequestId { get; private set; }
    public IReadOnlyList<FlowBreadcrumb> Breadcrumbs { get; private set; } = [];
    public string Trail { get; private set; } = string.Empty;
    public string BackPath { get; private set; } = "/flows";

    public async Task<IActionResult> OnGetAsync(
        Guid id,
        string? trail,
        bool published = false,
        bool present = false,
        Guid? requestId = null,
        CancellationToken cancellationToken = default)
    {
        StartInPresentation = present;
        if (requestId.HasValue)
        {
            if (!publications.CanReview) return Forbid();
            var snapshot = await publications.GetRequestDiagramAsync(requestId.Value, id, cancellationToken);
            if (snapshot is null) return NotFound();
            ConfigureReadOnly(snapshot);
            IsReviewPreview = true;
            ReviewRequestId = requestId;
            LoadUrl = $"/api/flows/publication-requests/{requestId.Value}/snapshot?flowId={id}";
            await LoadBreadcrumbsAsync(
                id,
                trail,
                (ancestorId, token) => publications.GetRequestDiagramAsync(requestId.Value, ancestorId, token),
                published: false,
                requestId: requestId,
                present: present,
                cancellationToken: cancellationToken);
            BackPath = Breadcrumbs.Count > 0
                ? Breadcrumbs[^1].Url
                : Url.Page("/Flows/Review") ?? "/flows/review";
            return Page();
        }

        if (published)
        {
            var publication = await publications.GetPublishedBySourceAsync(id, cancellationToken);
            if (publication is null) return NotFound();
            Configure(publication.Definition);
            IsPublishedView = true;
            CanEdit = publications.CanReview;
            CanRequestPublication = false;
            CanSaveAsTemplate = false;
            LoadUrl = $"/api/flows/published/{id}";
            SaveUrl = $"/api/flows/published/{id}";
            LinkTargetsUrl = $"/api/flows/published/{id}/link-targets";
            await LoadBreadcrumbsAsync(
                id,
                trail,
                async (ancestorId, token) =>
                    (await publications.GetPublishedBySourceAsync(ancestorId, token))?.Definition,
                published: true,
                requestId: null,
                present: present,
                cancellationToken: cancellationToken);
            BackPath = Breadcrumbs.Count > 0
                ? Breadcrumbs[^1].Url
                : (Url.Page("/Flows/Index") ?? "/flows") + "#published";
            return Page();
        }

        var flow = await flows.GetAsync(id, cancellationToken);
        if (flow is null) return NotFound();

        Configure(flow);
        CanEdit = permissions.CanEdit(flow.CreatedBy);
        IsShared = flow.IsShared;
        CanRequestPublication = CanEdit;
        CanSaveAsTemplate = permissions.CanManageTemplates();
        LoadUrl = $"/api/flows/{id}";
        SaveUrl = $"/api/flows/{id}";
        LinkTargetsUrl = $"/api/flows/{id}/link-targets";
        var latestRequest = await publications.GetLatestRequestAsync(id, cancellationToken);
        HasPendingPublicationRequest = latestRequest?.Status == FlowPublicationRequestStatus.Pending;
        LatestPublicationStatus = latestRequest?.Status;
        PublicationReviewNote = latestRequest?.ReviewNote;
        await LoadBreadcrumbsAsync(
            id,
            trail,
            (ancestorId, token) => flows.GetAsync(ancestorId, token),
            published: false,
            requestId: null,
            present: present,
            cancellationToken: cancellationToken);
        BackPath = Breadcrumbs.Count > 0
            ? Breadcrumbs[^1].Url
            : flow.DiagramType == DiagramType.WorkCenterWorkflow
                ? Url.Page("/WorkCenter/Configuration") ?? "/WorkCenter/Configuration"
                : Url.Page("/Flows/Index") ?? "/flows";
        return Page();
    }

    private void ConfigureReadOnly(FlowDefinition flow)
    {
        Configure(flow);
        CanEdit = false;
        IsShared = false;
        CanRequestPublication = false;
        CanSaveAsTemplate = false;
    }

    private void Configure(FlowDefinition flow)
    {
        FlowId = flow.Id;
        FlowName = flow.Name;
        DiagramType = flow.DiagramType;
    }

    private async Task LoadBreadcrumbsAsync(
        Guid currentId,
        string? trail,
        Func<Guid, CancellationToken, Task<FlowDefinition?>> loadAncestor,
        bool published,
        Guid? requestId,
        bool present,
        CancellationToken cancellationToken)
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
            var ancestor = await loadAncestor(id, cancellationToken);
            if (ancestor is null) break;
            var ancestorTrail = string.Join(',', prefix);
            var presentation = present ? true : (bool?)null;
            var url = requestId.HasValue
                ? Url.Page("/Flows/Edit", new { id, trail = ancestorTrail, requestId, present = presentation })
                : published
                    ? Url.Page("/Flows/Edit", new { id, trail = ancestorTrail, published = true, present = presentation })
                    : Url.Page("/Flows/Edit", new { id, trail = ancestorTrail, present = presentation });
            url ??= $"/flows/{id}/edit";
            breadcrumbs.Add(new FlowBreadcrumb(id, ancestor.Name, url));
            prefix.Add(id);
        }

        Breadcrumbs = breadcrumbs;
        Trail = string.Join(',', breadcrumbs.Select(item => item.Id));
    }
}

public sealed record FlowBreadcrumb(Guid Id, string Name, string Url);
