using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Notifications.Services;

namespace TeamHub.Notifications.Pages.Notifications;

[Authorize]
public sealed class OpenModel(IWorkflowNotificationInbox inbox) : PageModel
{
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var notification = await inbox.MarkReadAsync(
            id,
            User.Identity?.Name ?? string.Empty,
            cancellationToken);
        if (notification is null)
        {
            return NotFound();
        }

        var target = $"/Workflows/Details/{notification.WorkflowInstanceId}";
        if (notification.WorkflowStepInstanceId.HasValue)
        {
            target += $"#step-{notification.WorkflowStepInstanceId.Value}";
        }

        return LocalRedirect(target);
    }
}
