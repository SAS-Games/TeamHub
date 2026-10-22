using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TeamHub.Domain.Enums;
using TeamHub.Notifications.Models;
using TeamHub.Notifications.Persistence;

namespace TeamHub.Notifications.Services;

internal sealed partial class WorkflowNotificationInboxService(NotificationsDbContext dbContext)
    : IWorkflowNotificationInbox
{
    private static readonly NotificationType[] WorkflowTypes =
    [
        NotificationType.Assignment,
        NotificationType.Reminder,
        NotificationType.Overdue,
        NotificationType.Escalation,
        NotificationType.Completion,
        NotificationType.WorkflowCompleted
    ];

    public async Task<WorkflowNotificationInbox> GetUnreadAsync(
        string recipient,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var normalizedRecipient = NormalizeRecipient(recipient);
        if (normalizedRecipient.Length == 0 || limit <= 0)
        {
            return new WorkflowNotificationInbox(0, []);
        }

        var query = UnreadQuery(normalizedRecipient);
        var unreadCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(item => item.CreatedAtUtc)
            .Take(Math.Min(limit, 100))
            .ToListAsync(cancellationToken);

        return new WorkflowNotificationInbox(
            unreadCount,
            items.Select(ToNotification).ToList());
    }

    public async Task<WorkflowNotification?> MarkReadAsync(
        Guid notificationId,
        string recipient,
        CancellationToken cancellationToken = default)
    {
        var normalizedRecipient = NormalizeRecipient(recipient);
        if (normalizedRecipient.Length == 0)
        {
            return null;
        }

        var source = await dbContext.NotificationOutbox
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == notificationId
                && item.Recipient.ToLower() == normalizedRecipient
                && WorkflowTypes.Contains(item.Type), cancellationToken);
        if (source is null)
        {
            return null;
        }

        if (!await dbContext.ReadReceipts.AnyAsync(
            item => item.NotificationId == notificationId,
            cancellationToken))
        {
            dbContext.ReadReceipts.Add(new NotificationReadReceipt
            {
                NotificationId = notificationId,
                Recipient = normalizedRecipient,
                ReadAtUtc = DateTime.UtcNow
            });
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return ToNotification(source);
    }

    public async Task<int> MarkAllReadAsync(
        string recipient,
        CancellationToken cancellationToken = default)
    {
        var normalizedRecipient = NormalizeRecipient(recipient);
        if (normalizedRecipient.Length == 0)
        {
            return 0;
        }

        var unreadIds = await UnreadQuery(normalizedRecipient)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        if (unreadIds.Count == 0)
        {
            return 0;
        }

        var readAtUtc = DateTime.UtcNow;
        dbContext.ReadReceipts.AddRange(unreadIds.Select(id => new NotificationReadReceipt
        {
            NotificationId = id,
            Recipient = normalizedRecipient,
            ReadAtUtc = readAtUtc
        }));
        await dbContext.SaveChangesAsync(cancellationToken);
        return unreadIds.Count;
    }

    private IQueryable<NotificationInboxSource> UnreadQuery(string normalizedRecipient) =>
        dbContext.NotificationOutbox
            .AsNoTracking()
            .Where(item => item.WorkflowInstanceId != Guid.Empty
                && item.Recipient.ToLower() == normalizedRecipient
                && WorkflowTypes.Contains(item.Type)
                && !dbContext.ReadReceipts.Any(receipt => receipt.NotificationId == item.Id));

    private static WorkflowNotification ToNotification(NotificationInboxSource item) => new(
        item.Id,
        item.WorkflowInstanceId,
        item.WorkflowStepInstanceId,
        item.Type,
        string.IsNullOrWhiteSpace(item.Subject) ? TypeLabel(item.Type) : item.Subject.Trim(),
        Preview(item.Body),
        item.CreatedAtUtc);

    private static string NormalizeRecipient(string recipient) => recipient.Trim().ToLowerInvariant();

    private static string Preview(string body)
    {
        var compact = Whitespace().Replace(body ?? string.Empty, " ").Trim();
        return compact.Length <= 140 ? compact : $"{compact[..137]}...";
    }

    internal static string TypeLabel(NotificationType type) => type switch
    {
        NotificationType.Assignment => "Task assigned",
        NotificationType.Reminder => "Task reminder",
        NotificationType.Overdue => "Task overdue",
        NotificationType.Escalation => "Task escalation",
        NotificationType.Completion => "Task completed",
        NotificationType.WorkflowCompleted => "Workflow completed",
        _ => "Workflow notification"
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
