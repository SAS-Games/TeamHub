using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Authentication;
using TeamHub.MilestoneReview;
using TeamHub.Studio;
using TeamHub.Web.AccessControl;
using TeamHub.Web.Formatting;
using TeamHub.Web.Reviews;
using ReviewSnapshot = TeamHub.MilestoneReview.MilestoneReview;

namespace TeamHub.Web.Pages;

public sealed partial class MilestoneReviewModel(
    IMilestoneReviewService reviews,
    IMilestoneReviewUserDirectory reviewUsers,
    ICurrentAccessService access,
    IStudioJiraCommentService comments,
    IDataProtectionProvider protection,
    ISafeMarkdownRenderer markdown,
    ILogger<MilestoneReviewModel> logger) : PageModel
{
    private readonly IDataProtector restoreProtector = protection.CreateProtector("TeamHub.MilestoneReview.Restore.v1");

    [BindProperty]
    public List<string> RestoreSelections { get; set; } = [];

    public List<JiraFeedbackRestorePreview> RestorePreview { get; } = [];
    public List<string> RestoreWarnings { get; } = [];
    public bool ShowRestorePreview { get; private set; }

    public sealed record JiraFeedbackRestorePreview(
        string Reviewer, string Email, string CommentId, DateTimeOffset Created,
        string SessionDetails, string WhatGood, string NeedImprovements, string AdditionalFeedback, string Token);
    private sealed record RestoreToken(RestoreMilestoneReviewFeedbackRequest Request, DateTimeOffset Expires);

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

    [BindProperty(SupportsGet = true)]
    public Guid? EditReviewId { get; set; }

    [BindProperty]
    public Guid FeedbackId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? EditFeedbackId { get; set; }

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
    public ReviewSnapshot? EditingReview { get; private set; }
    public IReadOnlyList<ReviewSnapshot> HistoryReviews { get; private set; } = [];
    public IReadOnlyList<MilestoneReviewUser> AvailableReviewers { get; private set; } = [];
    public Guid? CurrentUserId { get; private set; }
    public bool HasFeedbackEditAccess { get; private set; }
    public bool IsAdmin => User.IsInRole(TeamHubUserTypes.Admin);

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (EditReviewId.HasValue && !IsAdmin) return Forbid();
        await LoadAsync(cancellationToken);
        if (EditFeedbackId.HasValue)
        {
            var feedback = CurrentReview?.Feedback.SingleOrDefault(item => item.Id == EditFeedbackId);
            if (feedback is null || !CanEdit(feedback)) return Forbid();
        }
        if (EditReviewId.HasValue)
        {
            if (EditingReview is null)
            {
                ErrorMessage = "The draft or published review was not found. Archived reviews cannot be edited.";
                return RedirectToPage(new { editReviewId = (Guid?)null });
            }
            ReviewId = EditingReview.Id;
            ProjectName = EditingReview.ProjectName;
            MilestoneName = EditingReview.MilestoneName;
            Description = EditingReview.Description;
            JiraTicketId = EditingReview.JiraTicketId;
            ReviewerIds = EditingReview.AssignedReviewers.Select(item => item.Id).ToList();
        }
        return Page();
    }

    public async Task<IActionResult> OnPostEditAsync(CancellationToken cancellationToken)
    {
        if (!IsAdmin) return Forbid();
        EditReviewId = ReviewId;
        if (!ValidateDetails())
        {
            await LoadAsync(cancellationToken);
            return Page();
        }
        try
        {
            await reviews.UpdateAsync(ReviewId, DetailsRequest(), IsAdmin, cancellationToken);
            StatusMessage = "Milestone review details updated. Existing feedback was preserved; Jira comments update on the next feedback save.";
            return RedirectToPage(new { editReviewId = (Guid?)null });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostPreviewRestoreAsync(CancellationToken cancellationToken)
    {
        if (!IsAdmin) return Forbid();
        await LoadAsync(cancellationToken);
        ShowRestorePreview = true;
        if (CurrentReview?.Id != ReviewId || string.IsNullOrWhiteSpace(CurrentReview.JiraTicketId))
        {
            ModelState.AddModelError(string.Empty, "Select the current published review with a Jira ticket.");
            return Page();
        }
        foreach (var reviewer in CurrentReview.AssignedReviewers)
        {
            var row = CurrentReview.Feedback.Single(item => item.ReviewerId == reviewer.Id);
            if (!string.IsNullOrWhiteSpace(row.SessionDetails + row.WhatGood + row.NeedImprovements + row.AdditionalFeedback)
                || row.JiraCommentId is not null)
            {
                RestoreWarnings.Add($"{reviewer.DisplayName}: existing feedback will not be overwritten.");
                continue;
            }
            try
            {
                var result = await comments.GetCommentsAsync(new(null, CurrentReview.JiraTicketId, reviewer.UserId, false),
                    cancellationToken);
                var count = RestorePreview.Count;
                foreach (var comment in result.Comments)
                {
                    var values = JiraMilestoneFeedbackParser.Parse(comment, reviewer.UserId);
                    if (values is null) continue;
                    var request = new RestoreMilestoneReviewFeedbackRequest(CurrentReview.Id, CurrentReview.JiraTicketId,
                        row.Id, row.Version, comment.Id, values[0], values[1], values[2], values[3]);
                    var token = restoreProtector.Protect(JsonSerializer.Serialize(
                        new RestoreToken(request, DateTimeOffset.UtcNow.AddMinutes(15))));
                    RestorePreview.Add(new(reviewer.DisplayName, reviewer.UserId, comment.Id, comment.Created,
                        values[0], values[1], values[2], values[3], token));
                }
                if (RestorePreview.Count == count)
                    RestoreWarnings.Add($"{reviewer.DisplayName}: no matching, supported TeamHub feedback comment was found. Comments with images or links must be reviewed manually using Show Jira Comments.");
            }
            catch (Exception exception) when (exception is StudioJiraCommentException or HttpRequestException
                or JsonException or InvalidOperationException or KeyNotFoundException or RegexMatchTimeoutException)
            {
                logger.LogWarning(exception, "Jira feedback restoration preview failed for reviewer {ReviewerId}.", reviewer.Id);
                RestoreWarnings.Add($"{reviewer.DisplayName}: could not load feedback. Check their personal Jira connection and ticket access, then retry.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                RestoreWarnings.Add($"{reviewer.DisplayName}: Jira timed out. Reopen the preview to retry.");
            }
        }
        return Page();
    }

    public async Task<IActionResult> OnPostRestoreAsync(CancellationToken cancellationToken)
    {
        if (!IsAdmin) return Forbid();
        try
        {
            var current = await reviews.GetCurrentAsync(cancellationToken);
            if (current?.Id != ReviewId || string.IsNullOrWhiteSpace(current.JiraTicketId))
                throw new InvalidOperationException("The published review changed. Reopen the restore preview.");
            var requests = new List<RestoreMilestoneReviewFeedbackRequest>();
            foreach (var selection in RestoreSelections.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                var token = JsonSerializer.Deserialize<RestoreToken>(restoreProtector.Unprotect(selection))
                    ?? throw new InvalidOperationException("The restore selection is invalid.");
                var request = token.Request;
                if (token.Expires < DateTimeOffset.UtcNow || request.ReviewId != current.Id
                    || !string.Equals(request.TicketId, current.JiraTicketId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The restore preview expired or the review changed. Reopen the preview.");
                var row = current.Feedback.SingleOrDefault(item => item.Id == request.FeedbackId)
                    ?? throw new InvalidOperationException("The feedback row changed. Reopen the preview.");
                var reviewer = current.AssignedReviewers.SingleOrDefault(item => item.Id == row.ReviewerId)
                    ?? throw new InvalidOperationException("The contributor selection changed. Reopen the preview.");
                var result = await comments.GetCommentsAsync(new(null, current.JiraTicketId, reviewer.UserId, false),
                    cancellationToken);
                var comment = result.Comments.SingleOrDefault(item => item.Id == request.CommentId);
                var values = comment is null ? null : JiraMilestoneFeedbackParser.Parse(comment, reviewer.UserId);
                if (values is null || !values.SequenceEqual(new[]
                    { request.SessionDetails, request.WhatGood, request.NeedImprovements, request.AdditionalFeedback }))
                    throw new InvalidOperationException("A selected Jira comment changed or is no longer accessible. Reopen the preview.");
                requests.Add(request);
            }
            await reviews.RestoreFeedbackAsync(requests, IsAdmin, cancellationToken);
            StatusMessage = $"Restored feedback for {requests.Count} reviewer(s). No Jira comments were changed.";
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            logger.LogWarning(exception, "Invalid Jira feedback restore selection.");
            ErrorMessage = "The restore selection is invalid. Reopen the preview and try again.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or StudioJiraCommentException)
        {
            ErrorMessage = exception.Message;
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Jira feedback restoration failed.");
            ErrorMessage = "Jira could not be reached. No feedback was restored; reopen the preview and try again.";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ErrorMessage = "Jira timed out. No feedback was restored; reopen the preview and try again.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateDraftAsync(CancellationToken cancellationToken)
    {
        if (!IsAdmin) return Forbid();
        if (!ValidateDetails())
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        try
        {
            await reviews.CreateDraftAsync(
                DetailsRequest(),
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
        if (!await access.CanAsync(TeamHubModules.MilestoneReview, AccessLevel.Edit, cancellationToken))
            return Forbid();
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

        var query = new StudioJiraCommentQuery(
            null, ticketId, User.Identity?.Name ?? "", User.IsInRole(TeamHubUserTypes.Privileged));
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
        HasFeedbackEditAccess && (IsAdmin || (CurrentUserId == feedback.ReviewerId
            && CurrentReview?.AssignedReviewers.Any(item => item.Id == feedback.ReviewerId) == true));

    public bool IsEditingFeedback(MilestoneReviewFeedback feedback) =>
        CanEdit(feedback) && EditFeedbackId == feedback.Id;

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        CurrentUserId = current.User?.Id;
        HasFeedbackEditAccess = await access.CanAsync(TeamHubModules.MilestoneReview, AccessLevel.Edit, cancellationToken);
        CurrentReview = await reviews.GetCurrentAsync(cancellationToken);
        HistoryReviews = await reviews.GetHistoryAsync(cancellationToken);
        if (IsAdmin)
        {
            Draft = await reviews.GetDraftAsync(cancellationToken);
            EditingReview = new[] { Draft, CurrentReview }.FirstOrDefault(item => item?.Id == EditReviewId);
            var available = (await reviewUsers.GetActiveUsersAsync(cancellationToken))
                .Where(item => item.IsActive && item.IsRegistered).ToList();
            foreach (var assigned in EditingReview?.AssignedReviewers ?? [])
            {
                if (available.All(item => item.Id != assigned.Id))
                    available.Add(new(assigned.Id, assigned.DisplayName, assigned.UserId, false, true));
            }
            AvailableReviewers = available;
        }
    }

    private CreateMilestoneReviewRequest DetailsRequest() =>
        new(ProjectName!, MilestoneName!, Description ?? string.Empty, JiraTicketId, ReviewerIds);

    private bool ValidateDetails()
    {
        if (ModelState.IsValid && ValidLength(ProjectName, 160) && ValidLength(MilestoneName, 160)
            && ValidLength(Description, 4000) && ValidLength(JiraTicketId, 64)
            && !string.IsNullOrWhiteSpace(ProjectName) && !string.IsNullOrWhiteSpace(MilestoneName))
            return true;
        ModelState.AddModelError(string.Empty, "Project name and milestone are required. Check the maximum field lengths.");
        return false;
    }

    private string CurrentActor() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? User.Identity?.Name
        ?? "Unknown";

    private static bool ValidLength(string? value, int maxLength) =>
        value is null || value.Length <= maxLength;
}
