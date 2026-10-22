namespace TeamHub.Notifications.Persistence;

internal sealed class NotificationReadReceipt
{
    public Guid NotificationId { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public DateTime ReadAtUtc { get; set; }
}
