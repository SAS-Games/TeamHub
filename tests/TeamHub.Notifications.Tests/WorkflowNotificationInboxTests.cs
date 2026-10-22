using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TeamHub.Domain.Enums;
using TeamHub.Notifications.Persistence;
using TeamHub.Notifications.Services;

namespace TeamHub.Notifications.Tests;

public sealed class WorkflowNotificationInboxTests
{
    [Fact]
    public async Task Inbox_ReturnsOnlyUnreadWorkflowNotificationsForCurrentRecipient()
    {
        await using var fixture = await NotificationFixture.CreateAsync();
        var visible = fixture.Add(NotificationType.Assignment, "Person@Example.com", "Assigned to you");
        fixture.Add(NotificationType.Reminder, "someone@example.com", "Someone else's reminder");
        fixture.Add(NotificationType.AccountInvitation, "person@example.com", "Account invitation");
        fixture.Add(NotificationType.Overdue, "person@example.com", "Not a workflow", Guid.Empty);
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.GetUnreadAsync("person@example.com");

        result.UnreadCount.Should().Be(1);
        result.Notifications.Should().ContainSingle(item => item.Id == visible.Id);
    }

    [Fact]
    public async Task OpeningNotification_MarksItReadAndIsIdempotent()
    {
        await using var fixture = await NotificationFixture.CreateAsync();
        var source = fixture.Add(NotificationType.Reminder, "person@example.com", "Task reminder");
        await fixture.Context.SaveChangesAsync();

        var opened = await fixture.Service.MarkReadAsync(source.Id, "PERSON@example.com");
        var openedAgain = await fixture.Service.MarkReadAsync(source.Id, "person@example.com");
        var remaining = await fixture.Service.GetUnreadAsync("person@example.com");

        opened.Should().NotBeNull();
        openedAgain.Should().NotBeNull();
        remaining.UnreadCount.Should().Be(0);
        (await fixture.Context.ReadReceipts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UserCannotReadAnotherRecipientsNotification()
    {
        await using var fixture = await NotificationFixture.CreateAsync();
        var source = fixture.Add(NotificationType.Escalation, "owner@example.com", "Escalated task");
        await fixture.Context.SaveChangesAsync();

        var opened = await fixture.Service.MarkReadAsync(source.Id, "other@example.com");

        opened.Should().BeNull();
        (await fixture.Context.ReadReceipts.CountAsync()).Should().Be(0);
    }

    private sealed class NotificationFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private NotificationFixture(SqliteConnection connection, NotificationsDbContext context)
        {
            this.connection = connection;
            Context = context;
            Service = new WorkflowNotificationInboxService(context);
        }

        public NotificationsDbContext Context { get; }
        public WorkflowNotificationInboxService Service { get; }

        public static async Task<NotificationFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<NotificationsDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new NotificationsDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new NotificationFixture(connection, context);
        }

        public NotificationInboxSource Add(
            NotificationType type,
            string recipient,
            string subject,
            Guid? workflowInstanceId = null)
        {
            var source = new NotificationInboxSource
            {
                WorkflowInstanceId = workflowInstanceId ?? Guid.NewGuid(),
                WorkflowStepInstanceId = Guid.NewGuid(),
                Type = type,
                Recipient = recipient,
                Subject = subject,
                Body = "Notification body",
                CreatedAtUtc = DateTime.UtcNow
            };
            Context.NotificationOutbox.Add(source);
            return source;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
