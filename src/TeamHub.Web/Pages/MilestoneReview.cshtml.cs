using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Authentication;
using TeamHub.MilestoneReview;
using TeamHub.Studio;
using TeamHub.Web.AccessControl;
using TeamHub.Web.Formatting;
using ReviewSnapshot = TeamHub.MilestoneReview.MilestoneReview;

namespace TeamHub.Web.Pages;

public sealed partial class MilestoneReviewModel(
    IMilestoneReviewService reviews,
    IMilestoneReviewUserDirectory reviewUsers,
    ICurrentAccessService access,
    IStudioJiraCommentService comments,
    IStudioDirectoryService studioDirectory,
    IAtlassianConfigurationService configuration,
    ISafeMarkdownRenderer markdown,
    ILogger<MilestoneReviewModel> logger) : PageModel
{
    [BindProperty]
    public string? ProjectName { get; set; }

    [BindProperty]
    public string? MilestoneName { get; set; }

    [BindProperty]
    public string? Description { get; set; }

    [BindProperty]
    public string? JiraTicketId { get; set; }

    [BindProperty]
    public List<Guid> ReviewerIds { get; set; } = [];

    [BindProperty]
    public Guid ReviewId { get; set; }

    [BindProperty]
    public Guid FeedbackId { get; set; }

    [BindProperty]
    public int Version { get; set; }

    [BindProperty]
    public string? WhatGood { get; set; }

    [BindProperty]
    public string? SessionDetails { get; set; }

    [BindProperty]
    public string? NeedImprovements { get; set; }

    [BindProperty]
    public string? AdditionalFeedback { get; set; }

    public ReviewSnapshot? CurrentReview { get; private set; }
    public ReviewSnapshot? Draft { get; private set; }
    public IReadOnlyList<ReviewSnapshot> HistoryReviews { get; private set; } = [];
    public IReadOnlyList<MilestoneReviewUser> AvailableReviewers { get; private set; } = [];
    public Guid? CurrentUserId { get; private set; }
    public bool IsAdmin => User.IsInRole(TeamHubUserTypes.Admin);

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostCreateDraftAsync(CancellationToken cancellationToken)
    {
        if (!IsAdmin) return Forbid();
        if (!ModelState.IsValid
            || !ValidLength(ProjectName, 160)
            || !ValidLength(MilestoneName, 160)
            || !ValidLength(Description, 4000)
            || !ValidLength(JiraTicketId, 64)
            || string.IsNullOrWhiteSpace(ProjectName)
            || string.IsNullOrWhiteSpace(MilestoneName))
        {
            ModelState.AddModelError(string.Empty, "Project and milestone names are required. Check the maximum field lengths.");
            await LoadAsync(cancellationToken);
            return Page();
        }

        try
        {
            await reviews.CreateDraftAsync(
                new CreateMilestoneReviewRequest(ProjectName!, MilestoneName!, Description ?? string.Empty,
                    JiraTicketId, ReviewerIds),
                CurrentActor(),
                cancellationToken);
            StatusMessage = "Milestone review draft created. Review the details and publish it when ready.";
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostPublishAsync(CancellationToken cancellationToken)
    {
        if (!IsAdmin) return Forbid();
        var published = await reviews.PublishDraftAsync(CurrentActor(), cancellationToken);
        if (published is null)
            ErrorMessage = "There is no draft to publish.";
        else
            StatusMessage = $"Milestone review published: {published.ProjectName} — {published.MilestoneName}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDiscardDraftAsync(CancellationToken cancellationToken)
    {
        if (!IsAdmin) return Forbid();
        await reviews.DiscardDraftAsync(cancellationToken);
        StatusMessage = "Milestone review draft discarded.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostArchiveAsync(CancellationToken cancellationToken)
    {
        if (!IsAdmin) return Forbid();
        try
        {
            await reviews.ArchiveAsync(ReviewId, IsAdmin, cancellationToken);
            StatusMessage = "Milestone review archived. Its feedback is available read-only in Review History.";
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken cancellationToken)
    {
        if (!IsAdmin) return Forbid();
        try
        {
            await reviews.DeleteAsync(ReviewId, IsAdmin, cancellationToken);
            StatusMessage = "Milestone review and its saved feedback permanently deleted from TeamHub. Jira comments were not deleted.";
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSaveFeedbackAsync(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current.User is null)
            return Forbid();
        if (!ValidLength(SessionDetails, 20000)
            || !ValidLength(WhatGood, 20000)
            || !ValidLength(NeedImprovements, 20000)
            || !ValidLength(AdditionalFeedback, 20000))
        {
            ErrorMessage = "Feedback was not saved. Check that each field is no longer than 20,000 characters.";
            return RedirectToPage();
        }
        try
        {
            await reviews.SaveFeedbackAsync(
                new SaveMilestoneReviewFeedbackRequest(
                    ReviewId, FeedbackId, Version,
                    SessionDetails ?? string.Empty, WhatGood ?? string.Empty,
                    NeedImprovements ?? string.Empty, AdditionalFeedback ?? string.Empty),
                current.User.Id,
                IsAdmin,
                cancellationToken);
            StatusMessage = "Your feedback was saved.";
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (MilestoneReviewCommentSyncException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException or KeyNotFoundException)
        {
            ErrorMessage = exception.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetFeedbackAsync(Guid reviewId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!await access.CanAsync(TeamHubModules.MilestoneReview, AccessLevel.ReadOnly, cancellationToken))
            return Forbid();
        if (reviewId == Guid.Empty)
            return JiraFailure("Select a valid milestone review.", StatusCodes.Status400BadRequest);

        var current = await reviews.GetCurrentAsync(cancellationToken);
        ReviewSnapshot? target = current?.Id == reviewId ? current : null;
        if (target is null)
        {
            var history = await reviews.GetHistoryAsync(cancellationToken);
            target = history.FirstOrDefault(item => item.Id == reviewId);
        }

        if (target is null)
            return JiraFailure("The requested milestone review was not found.", StatusCodes.Status404NotFound);

        var comments = target.Feedback
            .Where(fb => !string.IsNullOrWhiteSpace(fb.SessionDetails)
                || !string.IsNullOrWhiteSpace(fb.WhatGood)
                || !string.IsNullOrWhiteSpace(fb.NeedImprovements)
                || !string.IsNullOrWhiteSpace(fb.AdditionalFeedback))
            .Select(fb =>
            {
                var sb = new System.Text.StringBuilder();
                if (!string.IsNullOrWhiteSpace(fb.SessionDetails))
                {
                    sb.AppendLine("Session Details:");
                    sb.AppendLine(fb.SessionDetails.Trim());
                    sb.AppendLine();
                }
                if (!string.IsNullOrWhiteSpace(fb.WhatGood))
                {
                    sb.AppendLine("What's Good:");
                    sb.AppendLine(fb.WhatGood.Trim());
                    sb.AppendLine();
                }
                if (!string.IsNullOrWhiteSpace(fb.NeedImprovements))
                {
                    sb.AppendLine("Needs Improvement:");
                    sb.AppendLine(fb.NeedImprovements.Trim());
                    sb.AppendLine();
                }
                if (!string.IsNullOrWhiteSpace(fb.AdditionalFeedback))
                {
                    sb.AppendLine("Additional Feedback:");
                    sb.AppendLine(fb.AdditionalFeedback.Trim());
                }

                return new
                {
                    id = fb.Id.ToString("D"),
                    author = fb.Reviewer,
                    created = target.PublishedAt ?? target.CreatedAt,
                    updated = (DateTimeOffset?)null,
                    parts = new[] { new
                    {
                        kind = "markdown",
                        text = sb.ToString().TrimEnd(),
                        html = markdown.ToHtml(sb.ToString().TrimEnd())
                    } }
                };
            }).ToList();

        return new JsonResult(new
        {
            ticketId = string.IsNullOrWhiteSpace(target.JiraTicketId)
                ? $"{target.ProjectName} — {target.MilestoneName}"
                : target.JiraTicketId,
            ticketUrl = string.Empty,
            isUsingSharedCredential = false,
            comments
        });
    }

    public async Task<IActionResult> OnGetCommentsAsync(string ticketId, CancellationToken cancellationToken) =>
        await ReadJiraAsync(ticketId, isImage: false, null, null, cancellationToken);

    public async Task<IActionResult> OnGetImageAsync(
        string ticketId, string commentId, string attachmentId, CancellationToken cancellationToken) =>
        await ReadJiraAsync(ticketId, isImage: true, commentId, attachmentId, cancellationToken);

    public async Task<IActionResult> OnGetCommentImageAsync(
        string ticketId, string commentId, string attachmentId, CancellationToken cancellationToken) =>
        await ReadJiraAsync(ticketId, isImage: true, commentId, attachmentId, cancellationToken);

    private async Task<IActionResult> ReadJiraAsync(
        string ticketId, bool isImage, string? commentId, string? attachmentId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!await access.CanAsync(TeamHubModules.MilestoneReview, AccessLevel.ReadOnly, cancellationToken))
            return Forbid();
        if (string.IsNullOrWhiteSpace(ticketId) || !TicketKey().IsMatch(ticketId))
            return JiraFailure("Select a valid Jira ticket.", StatusCodes.Status400BadRequest);

        var prefix = ticketId[..ticketId.LastIndexOf('-')];
        var candidates = new List<string>();
        foreach (var studio in (await studioDirectory.GetStudiosAsync(cancellationToken)).Where(item => item.IsActive))
        {
            var mapping = await configuration.GetStudioMappingAsync(studio.Id, cancellationToken);
            if (mapping is not null
                && mapping.JiraProjectKeys.Contains(prefix, StringComparer.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(mapping.JiraStudioComponent))
            {
                candidates.Add(studio.Id);
            }
        }
        if (candidates.Count != 1)
        {
            return JiraFailure(candidates.Count == 0
                ? "The Jira ticket project is not mapped to an active studio."
                : "The Jira ticket project maps to multiple studios. Configure a unique project mapping before viewing comments.",
                StatusCodes.Status400BadRequest);
        }

        var studioId = candidates[0];
        var query = new StudioJiraCommentQuery(
            studioId, ticketId, User.Identity?.Name ?? "", User.IsInRole(TeamHubUserTypes.Privileged));
        try
        {
            if (isImage)
            {
                if (string.IsNullOrWhiteSpace(commentId) || string.IsNullOrWhiteSpace(attachmentId))
                    return JiraFailure("Select a valid comment image.", StatusCodes.Status400BadRequest);
                var image = await comments.GetImageAsync(query, commentId, attachmentId, cancellationToken);
                return File(image.Content, image.ContentType);
            }
            return new JsonResult(await comments.GetCommentsAsync(query, cancellationToken));
        }
        catch (StudioJiraCommentException exception)
        {
            return JiraFailure(exception.Message, StatusCodes.Status400BadRequest);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Jira comment preview request failed.");
            return JiraFailure("Jira could not be reached. Please try again shortly.", StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return JiraFailure("Jira comment preview timed out. Please try again shortly.", StatusCodes.Status504GatewayTimeout);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or RegexMatchTimeoutException)
        {
            logger.LogWarning(exception, "Jira returned an invalid comment preview response.");
            return JiraFailure("Jira returned a comment or image response that could not be read safely. Open the ticket in Jira.",
                StatusCodes.Status502BadGateway);
        }
    }

    private static JsonResult JiraFailure(string message, int statusCode) =>
        new(new { error = message }) { StatusCode = statusCode };

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]+-\\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex TicketKey();

    public bool CanEdit(MilestoneReviewFeedback feedback) =>
        IsAdmin || CurrentUserId == feedback.ReviewerId;

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        CurrentUserId = current.User?.Id;
        CurrentReview = await reviews.GetCurrentAsync(cancellationToken);
        HistoryReviews = await reviews.GetHistoryAsync(cancellationToken);
        if (IsAdmin)
        {
            Draft = await reviews.GetDraftAsync(cancellationToken);
            AvailableReviewers = await reviewUsers.GetActiveUsersAsync(cancellationToken);
        }
    }

    private string CurrentActor() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? User.Identity?.Name
        ?? "Unknown";

    private static bool ValidLength(string? value, int maxLength) =>
        value is null || value.Length <= maxLength;
}
