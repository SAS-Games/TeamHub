using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Application.Interfaces;
using TeamHub.Authentication;
using TeamHub.Domain.Enums;

namespace TeamHub.Web.Pages;

[AllowAnonymous]
public sealed class ForgotPasswordModel(
    IUserAccessService users,
    INotificationService notifications,
    ILogger<ForgotPasswordModel> logger) : PageModel
{
    [BindProperty]
    public string Identifier { get; set; } = string.Empty;

    public bool Submitted { get; private set; }

    public async Task OnPostAsync(CancellationToken cancellationToken)
    {
        Submitted = true;
        try
        {
            var reset = await users.CreatePasswordResetAsync(Identifier, cancellationToken);
            if (reset is null) return;
            var link = Url.Page("/ResetPassword", null, new { token = reset.Token }, Request.Scheme);
            if (string.IsNullOrWhiteSpace(link)) return;
            await notifications.SendAsync(new NotificationMessage
            {
                WorkflowInstanceId = Guid.Empty,
                Type = NotificationType.PasswordReset,
                Recipient = reset.Recipient,
                Subject = "Reset your TeamHub password",
                Body = $"Hello {reset.DisplayName},\n\nReset your TeamHub password using this single-use link:\n\n{link}\n\nThis link expires on {reset.ExpiresAt:dd MMM yyyy HH:mm} UTC. If you did not request a reset, ignore this email."
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not queue a TeamHub password-reset email");
        }
    }
}
