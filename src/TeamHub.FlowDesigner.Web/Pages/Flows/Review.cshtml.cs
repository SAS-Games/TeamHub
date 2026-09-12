using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Web.Pages.Flows;

public sealed class ReviewModel(IFlowPublicationWorkflowService publications) : PageModel
{
    public IReadOnlyList<FlowPublicationRequestSummary> Requests { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!publications.CanReview) return Forbid();
        Requests = await publications.ListPendingAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostApproveAsync(
        Guid id,
        string? reviewNote,
        CancellationToken cancellationToken)
    {
        if (!publications.CanReview) return Forbid();
        try
        {
            var published = await publications.ApproveAsync(id, reviewNote, cancellationToken);
            TempData["PublicationMessage"] = $"{published.Name} was published as version {published.PublicationVersion}.";
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            TempData["PublicationError"] = exception.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRejectAsync(
        Guid id,
        string reviewNote,
        CancellationToken cancellationToken)
    {
        if (!publications.CanReview) return Forbid();
        try
        {
            await publications.RejectAsync(id, reviewNote, cancellationToken);
            TempData["PublicationMessage"] = "The publication request was rejected and returned to its author.";
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            TempData["PublicationError"] = exception.Message;
        }
        return RedirectToPage();
    }
}
