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
    public EmailNotificationSettingsInput Settings { get; set; } = new();

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
            await LoadInvalidSaveStateAsync(cancellationToken);
            return Page();
        }
        try
        {
            await configuration.SaveSettingsAsync(new SaveEmailNotificationSettingsRequest
            {
                Settings = Settings.ToSettings(),
                Password = Password,
                RemovePassword = RemovePassword
            }, cancellationToken);
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadInvalidSaveStateAsync(cancellationToken);
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
        Settings = EmailNotificationSettingsInput.FromSettings(
            await configuration.GetSettingsAsync(cancellationToken));
        History = await configuration.GetDeliveryHistoryAsync(cancellationToken: cancellationToken);
    }

    private async Task LoadInvalidSaveStateAsync(CancellationToken cancellationToken)
    {
        Settings.HasPassword = (await configuration.GetSettingsAsync(cancellationToken)).HasPassword;
        History = await configuration.GetDeliveryHistoryAsync(cancellationToken: cancellationToken);
    }
}

public sealed class EmailNotificationSettingsInput
{
    public bool Enabled { get; set; }
    public string? Host { get; set; } = "smtp.office365.com";
    public int? Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? FromAddress { get; set; }
    public bool UseSsl { get; set; } = true;
    public bool HasPassword { get; set; }
    public string? DefaultCc { get; set; }
    public string? DefaultBcc { get; set; }
    public string? CompletionRecipient { get; set; }
    public int? MaxDeliveryAttempts { get; set; } = 3;
    public int? RetryDelayMinutes { get; set; } = 5;
    public string? AssignmentSubjectTemplate { get; set; } = "{Subject}";
    public string? AssignmentBodyTemplate { get; set; } = "{Body}";
    public string? ReminderSubjectTemplate { get; set; } = "{Subject}";
    public string? ReminderBodyTemplate { get; set; } = "{Body}";
    public string? EscalationSubjectTemplate { get; set; } = "{Subject}";
    public string? EscalationBodyTemplate { get; set; } = "{Body}";
    public string? CompletionSubjectTemplate { get; set; } = "{Subject}";
    public string? CompletionBodyTemplate { get; set; } = "{Body}";

    public EmailNotificationSettings ToSettings() => new()
    {
        Enabled = Enabled,
        Host = Host ?? string.Empty,
        Port = Port ?? 587,
        Username = Username ?? string.Empty,
        FromAddress = FromAddress ?? string.Empty,
        UseSsl = UseSsl,
        HasPassword = HasPassword,
        DefaultCc = DefaultCc ?? string.Empty,
        DefaultBcc = DefaultBcc ?? string.Empty,
        CompletionRecipient = CompletionRecipient ?? string.Empty,
        MaxDeliveryAttempts = MaxDeliveryAttempts ?? 3,
        RetryDelayMinutes = RetryDelayMinutes ?? 5,
        AssignmentSubjectTemplate = AssignmentSubjectTemplate ?? string.Empty,
        AssignmentBodyTemplate = AssignmentBodyTemplate ?? string.Empty,
        ReminderSubjectTemplate = ReminderSubjectTemplate ?? string.Empty,
        ReminderBodyTemplate = ReminderBodyTemplate ?? string.Empty,
        EscalationSubjectTemplate = EscalationSubjectTemplate ?? string.Empty,
        EscalationBodyTemplate = EscalationBodyTemplate ?? string.Empty,
        CompletionSubjectTemplate = CompletionSubjectTemplate ?? string.Empty,
        CompletionBodyTemplate = CompletionBodyTemplate ?? string.Empty
    };

    public static EmailNotificationSettingsInput FromSettings(EmailNotificationSettings settings) => new()
    {
        Enabled = settings.Enabled,
        Host = settings.Host,
        Port = settings.Port,
        Username = settings.Username,
        FromAddress = settings.FromAddress,
        UseSsl = settings.UseSsl,
        HasPassword = settings.HasPassword,
        DefaultCc = settings.DefaultCc,
        DefaultBcc = settings.DefaultBcc,
        CompletionRecipient = settings.CompletionRecipient,
        MaxDeliveryAttempts = settings.MaxDeliveryAttempts,
        RetryDelayMinutes = settings.RetryDelayMinutes,
        AssignmentSubjectTemplate = settings.AssignmentSubjectTemplate,
        AssignmentBodyTemplate = settings.AssignmentBodyTemplate,
        ReminderSubjectTemplate = settings.ReminderSubjectTemplate,
        ReminderBodyTemplate = settings.ReminderBodyTemplate,
        EscalationSubjectTemplate = settings.EscalationSubjectTemplate,
        EscalationBodyTemplate = settings.EscalationBodyTemplate,
        CompletionSubjectTemplate = settings.CompletionSubjectTemplate,
        CompletionBodyTemplate = settings.CompletionBodyTemplate
    };
}
