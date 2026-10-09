using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Authentication;
using TeamHub.Studio;
using TeamHub.Web.AccessControl;

namespace TeamHub.Web.Pages;

public sealed class SupportSummaryCommentsModel(
    IStudioJiraCommentService comments,
    ICurrentAccessService access,
    ILogger<SupportSummaryCommentsModel> logger) : PageModel
{
    public Task<IActionResult> OnGetAsync(string studioId, string ticketId, CancellationToken cancellationToken) =>
        ReadAsync(studioId, ticketId, false, null, null, cancellationToken);

    public Task<IActionResult> OnGetImageAsync(
        string studioId, string ticketId, string commentId, string attachmentId, CancellationToken cancellationToken) =>
        ReadAsync(studioId, ticketId, true, commentId, attachmentId, cancellationToken);

    private async Task<IActionResult> ReadAsync(
        string studioId, string ticketId, bool isImage, string? commentId, string? attachmentId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!await access.CanAsync(TeamHubModules.StudioSupport, AccessLevel.ReadOnly, cancellationToken))
            return Forbid();
        if (!ModelState.IsValid)
            return Failure("Select a valid studio and ticket.", StatusCodes.Status400BadRequest);
        var query = new StudioJiraCommentQuery(studioId, ticketId, User.Identity?.Name ?? "",
            User.IsInRole(TeamHubUserTypes.Privileged));
        try
        {
            if (isImage)
            {
                if (string.IsNullOrWhiteSpace(commentId) || string.IsNullOrWhiteSpace(attachmentId))
                    return Failure("Select a valid comment image.", StatusCodes.Status400BadRequest);
                var image = await comments.GetImageAsync(query, commentId, attachmentId, cancellationToken);
                return File(image.Content, image.ContentType);
            }
            return new JsonResult(await comments.GetCommentsAsync(query, cancellationToken));
        }
        catch (StudioJiraCommentException exception)
        {
            return Failure(exception.Message, StatusCodes.Status400BadRequest);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Jira comment preview request failed.");
            return Failure("Jira could not be reached. Please try again shortly.", StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("Jira comment preview timed out. Please try again shortly.", StatusCodes.Status504GatewayTimeout);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or RegexMatchTimeoutException)
        {
            logger.LogWarning(exception, "Jira returned an invalid comment preview response.");
            return Failure("Jira returned a comment or image response that could not be read safely. Open the ticket in Jira.",
                StatusCodes.Status502BadGateway);
        }
    }

    private static JsonResult Failure(string message, int statusCode) =>
        new(new { error = message }) { StatusCode = statusCode };
}
