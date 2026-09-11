using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Application.Interfaces;
using TeamHub.Domain.Enums;

namespace TeamHub.Web.Pages.Configuration;

[Authorize(Roles = "Admin")]
public sealed class EmailModel(
    IEmailNotificationConfigurationService configuration,
    INotificationService notifications) : PageModel
{
    [BindProperty]
    public EmailNotificationSettings Settings { get; set; } = new();

    [BindProperty, DataType(DataType.Password)]
    public string? Password { get; set; }

    [BindProperty]
    public bool RemovePassword { get; set; }

    [BindProperty, EmailAddress]
    public string? TestRecipient { get; set; }

    public IReadOnlyList<EmailDeliveryRecord> History { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            History = await configuration.GetDeliveryHistoryAsync(cancellationToken: cancellationToken);
            return Page();
        }
        try
        {
            await configuration.SaveSettingsAsync(new SaveEmailNotificationSettingsRequest
            {
                Settings = Settings,
                Password = Password,
                RemovePassword = RemovePassword
            }, cancellationToken);
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            History = await configuration.GetDeliveryHistoryAsync(cancellationToken: cancellationToken);
            return Page();
        }
        StatusMessage = "Email configuration saved securely.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(TestRecipient))
        {
            StatusMessage = "Enter a valid test recipient.";
            return RedirectToPage();
        }
        await notifications.SendAsync(new NotificationMessage
        {
            Type = NotificationType.Assignment,
            Recipient = TestRecipient,
            Subject = "TeamHub Work Center email test",
            Body = "This is a test notification from TeamHub Work Center."
        }, cancellationToken);
        StatusMessage = "Test notification queued. Check Delivery History for its result.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRetryAsync(Guid id, CancellationToken cancellationToken)
    {
        await configuration.RetryAsync(id, cancellationToken);
        StatusMessage = "Notification queued for another delivery attempt.";
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Settings = await configuration.GetSettingsAsync(cancellationToken);
        History = await configuration.GetDeliveryHistoryAsync(cancellationToken: cancellationToken);
    }
}
