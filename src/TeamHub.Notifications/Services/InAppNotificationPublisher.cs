using Microsoft.EntityFrameworkCore;
using TeamHub.Notifications.Persistence;

namespace TeamHub.Notifications.Services;

internal sealed class InAppNotificationPublisher(NotificationsDbContext dbContext)
    : IInAppNotificationPublisher
{
    public async Task PublishAsync(
        InAppNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Id == Guid.Empty) throw new ArgumentException("A notification ID is required.", nameof(request));
        var recipient = Required(request.Recipient, 320, "Recipient");
        var subject = Required(request.Subject, 1_000, "Subject");
        var body = Required(request.Body, 20_000, "Body");
        if (await dbContext.NotificationOutbox.AsNoTracking()
            .AnyAsync(item => item.Id == request.Id, cancellationToken)) return;

        var now = DateTime.UtcNow;
        dbContext.NotificationOutbox.Add(new NotificationInboxSource
        {
            Id = request.Id,
            WorkflowInstanceId = Guid.Empty,
            Type = request.Type,
            Recipient = recipient,
            Subject = subject,
            Body = body,
            Status = "InAppOnly",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string Required(string? value, int maximumLength, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maximumLength)
            throw new ArgumentException($"{name} must contain 1 to {maximumLength} characters.", name);
        return normalized;
    }
}
