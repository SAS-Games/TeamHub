namespace TeamHub.Domain.Entities;

public sealed class EmailNotificationSettingsRecord
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public string Host { get; set; } = "mrelay.noc.sony.co.jp";
    public int Port { get; set; } = 25;
    public string Username { get; set; } = string.Empty;
    public string? PasswordProtected { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public bool UseSsl { get; set; }
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
    public DateTime UpdatedAtUtc { get; set; }
}
