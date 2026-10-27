using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Notifications.Models;
using TeamHub.Notifications.Services;

namespace TeamHub.Notifications.Pages.Notifications;

[Authorize]
public sealed class IndexModel(IWorkflowNotificationInbox inbox) : PageModel
{
    public WorkflowNotificationInbox Inbox { get; private set; } = new(0, []);

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Inbox = await inbox.GetUnreadAsync(CurrentUser(), 100, cancellationToken);

    public async Task<IActionResult> OnGetInboxAsync(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        var current = await inbox.GetUnreadAsync(CurrentUser(), 8, cancellationToken);
        return new JsonResult(new
        {
            unreadCount = current.UnreadCount,
            notifications = current.Notifications.Select(notification => new
            {
                id = notification.Id,
                type = notification.Type.ToString(),
                notification.Subject,
                notification.BodyPreview,
                notification.CreatedAtUtc,
                openUrl = $"/Notifications/Open/{notification.Id}"
            })
        });
    }

    public async Task<IActionResult> OnPostMarkAllReadAsync(CancellationToken cancellationToken)
    {
        await inbox.MarkAllReadAsync(CurrentUser(), cancellationToken);
        return RedirectToPage();
    }

    private string CurrentUser() => User.Identity?.Name ?? string.Empty;
}
