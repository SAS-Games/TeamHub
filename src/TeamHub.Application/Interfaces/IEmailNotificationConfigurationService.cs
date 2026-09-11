using TeamHub.Domain.Enums;

namespace TeamHub.Application.Interfaces;

public sealed class EmailNotificationSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "smtp.office365.com";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public bool UseSsl { get; set; } = true;
    public bool HasPassword { get; set; }
    public string DefaultCc { get; set; } = string.Empty;
    public string DefaultBcc { get; set; } = string.Empty;
    public string CompletionRecipient { get; set; } = string.Empty;
    public int MaxDeliveryAttempts { get; set; } = 3;
    public int RetryDelayMinutes { get; set; } = 5;
    public string AssignmentSubjectTemplate { get; set; } = "{Subject}";
    public string AssignmentBodyTemplate { get; set; } = "{Body}";
    public string ReminderSubjectTemplate { get; set; } = "{Subject}";
    public string ReminderBodyTemplate { get; set; } = "{Body}";
    public string EscalationSubjectTemplate { get; set; } = "{Subject}";
    public string EscalationBodyTemplate { get; set; } = "{Body}";
    public string CompletionSubjectTemplate { get; set; } = "{Subject}";
    public string CompletionBodyTemplate { get; set; } = "{Body}";
}

public sealed class SaveEmailNotificationSettingsRequest
{
    public EmailNotificationSettings Settings { get; set; } = new();
    public string? Password { get; set; }
    public bool RemovePassword { get; set; }
}

public sealed record EmailDeliveryRecord(
    Guid Id,
    DateTime CreatedAtUtc,
    DateTime? SentAtUtc,
    NotificationType Type,
    string Recipient,
    string Subject,
    string Status,
    int Attempts,
    string? ErrorMessage);

public interface IEmailNotificationConfigurationService
{
    Task<EmailNotificationSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(SaveEmailNotificationSettingsRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EmailDeliveryRecord>> GetDeliveryHistoryAsync(int count = 100, CancellationToken cancellationToken = default);
    Task RetryAsync(Guid deliveryId, CancellationToken cancellationToken = default);
}

public interface INotificationRecipientResolver
{
    Task<string?> ResolveEmailAsync(string recipient, CancellationToken cancellationToken = default);
}
