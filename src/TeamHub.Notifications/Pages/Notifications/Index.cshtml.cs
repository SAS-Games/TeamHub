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

    public async Task<IActionResult> OnPostMarkAllReadAsync(CancellationToken cancellationToken)
    {
        await inbox.MarkAllReadAsync(CurrentUser(), cancellationToken);
        return RedirectToPage();
    }

    private string CurrentUser() => User.Identity?.Name ?? string.Empty;
}
