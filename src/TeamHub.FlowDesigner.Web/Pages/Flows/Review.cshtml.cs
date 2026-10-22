using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Web.Pages.Flows;

public sealed class ReviewModel(IFlowPublicationWorkflowService publications) : PageModel
{
    public sealed record ReviewItem(FlowPublicationRequestSummary Request, IReadOnlyList<FlowPublicationReviewer> Reviewers)
    {
        public bool AllReviewersApproved => Reviewers.All(item => item.Status == FlowPublicationReviewerStatus.Approved);
    }

    public IReadOnlyList<ReviewItem> Requests { get; private set; } = [];
    public bool IsAdmin => publications.CanReview;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var requests = await publications.ListPendingAsync(cancellationToken);
        var items = new List<ReviewItem>(requests.Count);
        foreach (var request in requests)
            items.Add(new(request, await publications.ListReviewersAsync(request.Id, cancellationToken)));
        Requests = items;
        return Page();
    }

    public async Task<IActionResult> OnPostApproveReviewAsync(
        Guid id,
        string? reviewNote,
        CancellationToken cancellationToken)
    {
        try
        {
            await publications.ApproveReviewAsync(id, reviewNote, cancellationToken);
            TempData["PublicationMessage"] = "Your review was approved. An administrator can publish after every reviewer is done.";
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            TempData["PublicationError"] = exception.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostApproveAsync(
        Guid id,
        string? reviewNote,
        CancellationToken cancellationToken)
    {
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
