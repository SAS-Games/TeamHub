namespace TeamHub.Infrastructure.Options;

public sealed class EmailNotificationOptions
{
    public const string SectionName = "EmailNotification";
    public bool Enabled { get; set; }
    public string Host { get; set; } = "smtp.office365.com";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = "workflow-poc@localhost";
    public bool UseSsl { get; set; } = true;
    public bool FallbackToDatabaseLogging { get; set; } = true;
}
