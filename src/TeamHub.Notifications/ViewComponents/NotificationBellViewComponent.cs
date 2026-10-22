using Microsoft.AspNetCore.Mvc;
using TeamHub.Notifications.Models;
using TeamHub.Notifications.Services;

namespace TeamHub.Notifications.ViewComponents;

public sealed class NotificationBellViewComponent(IWorkflowNotificationInbox inbox) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (UserClaimsPrincipal.Identity?.IsAuthenticated != true)
        {
            return Content(string.Empty);
        }

        var recipient = UserClaimsPrincipal.Identity.Name ?? string.Empty;
        WorkflowNotificationInbox model;
        try
        {
            model = await inbox.GetUnreadAsync(recipient, 8, HttpContext.RequestAborted);
        }
        catch
        {
            model = new WorkflowNotificationInbox(0, []);
        }

        return View(model);
    }
}
