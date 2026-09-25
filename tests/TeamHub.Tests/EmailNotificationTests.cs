using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TeamHub.Application.Interfaces;
using TeamHub.Domain.Enums;
using TeamHub.Infrastructure.Persistence;
using TeamHub.Infrastructure.Services;

namespace TeamHub.Tests;

public sealed class EmailNotificationTests
{
    [Fact]
    public async Task Settings_EncryptThePasswordAndNeverReturnIt()
    {
        await using var context = CreateDbContext();
        var service = new EmailNotificationConfigurationService(context, new EphemeralDataProtectionProvider());
        const string password = "smtp-secret-value";

        await service.SaveSettingsAsync(new SaveEmailNotificationSettingsRequest
        {
            Settings = ValidSettings(),
            Password = password
        });

        var stored = await context.EmailNotificationSettings.SingleAsync();
        stored.PasswordProtected.Should().NotBeNullOrWhiteSpace();
        stored.PasswordProtected.Should().NotContain(password);
        var returned = await service.GetSettingsAsync();
        returned.HasPassword.Should().BeTrue();
        returned.GetType().GetProperty("Password").Should().BeNull();
    }

    [Fact]
    public async Task RelaySettings_DoNotRequireOrRetainCredentials()
    {
        await using var context = CreateDbContext();
        var service = new EmailNotificationConfigurationService(context, new EphemeralDataProtectionProvider());
        var authenticated = ValidSettings();
        await service.SaveSettingsAsync(new SaveEmailNotificationSettingsRequest
        {
            Settings = authenticated,
            Password = "smtp-secret-value"
        });

        authenticated.Host = "mrelay.noc.sony.co.jp";
        authenticated.Port = 25;
        authenticated.UseAuthentication = false;
        authenticated.UseSsl = false;
        await service.SaveSettingsAsync(new SaveEmailNotificationSettingsRequest { Settings = authenticated });

        var stored = await context.EmailNotificationSettings.SingleAsync();
        stored.Username.Should().BeEmpty();
        stored.PasswordProtected.Should().BeNull();
        var returned = await service.GetSettingsAsync();
        returned.UseAuthentication.Should().BeFalse();
        returned.HasPassword.Should().BeFalse();
    }

    [Fact]
    public void RelayClient_UsesPlainSmtpWithoutAnyCredentials()
    {
        var settings = new EmailNotificationSettings
        {
            Host = "mrelay.noc.sony.co.jp",
            Port = 25,
            UseAuthentication = false,
            UseSsl = false
        };

        using var client = SmtpEmailOutboxProcessor.CreateClient(settings, string.Empty);

        client.Host.Should().Be("mrelay.noc.sony.co.jp");
        client.Port.Should().Be(25);
        client.EnableSsl.Should().BeFalse();
        client.UseDefaultCredentials.Should().BeFalse();
        client.Credentials.Should().BeNull();
    }

    [Fact]
    public async Task Notification_IsResolvedTemplatedAndStoredInDurableOutbox()
    {
        await using var context = CreateDbContext();
        var settings = ValidSettings();
        settings.AssignmentSubjectTemplate = "[TeamHub] {Subject}";
        settings.AssignmentBodyTemplate = "Hello {Recipient}\n\n{Body}";
        settings.DefaultCc = "manager@example.com";
        var service = new SmtpNotificationService(
            context,
            new FixedClock(new DateTime(2026, 9, 11, 10, 0, 0, DateTimeKind.Utc)),
            new StubConfiguration(settings),
            new StubResolver("owner@example.com"),
            NullLogger<SmtpNotificationService>.Instance);

        await service.SendAsync(new NotificationMessage
        {
            WorkflowInstanceId = Guid.NewGuid(),
            Type = NotificationType.Assignment,
            Recipient = "OWNER_USER",
            Subject = "Action Required",
            Body = "Complete the task."
        });

        var queued = await context.NotificationOutbox.SingleAsync();
        queued.Status.Should().Be("Pending");
        queued.Recipient.Should().Be("owner@example.com");
        queued.Cc.Should().Be("manager@example.com");
        queued.Subject.Should().Be("[TeamHub] Action Required");
        queued.Body.Should().Contain("Complete the task.");
    }

    [Fact]
    public async Task DisabledEmail_IsRecordedWithoutBeingQueuedForSmtp()
    {
        await using var context = CreateDbContext();
        var settings = ValidSettings();
        settings.Enabled = false;
        var service = new SmtpNotificationService(
            context,
            new FixedClock(DateTime.UtcNow),
            new StubConfiguration(settings),
            new StubResolver("owner@example.com"),
            NullLogger<SmtpNotificationService>.Instance);

        await service.SendAsync(new NotificationMessage
        {
            Type = NotificationType.Reminder,
            Recipient = "owner@example.com",
            Subject = "Reminder",
            Body = "Task due."
        });

        var recorded = await context.NotificationOutbox.SingleAsync();
        recorded.Status.Should().Be("LoggedOnly");
        recorded.LastError.Should().Contain("disabled");
    }

    [Fact]
    public async Task DatabaseInitializer_CreatesEmailSettingsAndOutboxForSqlite()
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        await using var context = new WorkflowDbContext(options);
        await context.Database.OpenConnectionAsync();

        await new WorkflowDatabaseInitializer(context).InitializeAsync();

        var settings = await context.EmailNotificationSettings.SingleAsync();
        settings.Host.Should().Be("mrelay.noc.sony.co.jp");
        settings.Port.Should().Be(25);
        settings.UseSsl.Should().BeFalse();
        (await context.NotificationOutbox.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DatabaseInitializer_MigratesLegacyOffice365SettingsToRelay()
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        await using var context = new WorkflowDbContext(options);
        await context.Database.OpenConnectionAsync();
        await new WorkflowDatabaseInitializer(context).InitializeAsync();
        var settings = await context.EmailNotificationSettings.SingleAsync();
        settings.Host = "smtp.office365.com";
        settings.Port = 587;
        settings.Username = "sender@example.com";
        settings.PasswordProtected = "protected-value";
        settings.FromAddress = "sender@example.com";
        settings.UseSsl = true;
        await context.SaveChangesAsync();

        await new WorkflowDatabaseInitializer(context).InitializeAsync();
        context.ChangeTracker.Clear();

        settings = await context.EmailNotificationSettings.SingleAsync();
        settings.Host.Should().Be("mrelay.noc.sony.co.jp");
        settings.Port.Should().Be(25);
        settings.Username.Should().BeEmpty();
        settings.PasswordProtected.Should().BeNull();
        settings.FromAddress.Should().Be("sender@example.com");
        settings.UseSsl.Should().BeFalse();
    }

    private static WorkflowDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static EmailNotificationSettings ValidSettings() => new()
    {
        Enabled = true,
        Host = "smtp.example.com",
        Port = 587,
        UseAuthentication = true,
        Username = "sender@example.com",
        FromAddress = "sender@example.com",
        UseSsl = true
    };

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class StubResolver(string resolved) : INotificationRecipientResolver
    {
        public Task<string?> ResolveEmailAsync(string recipient, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(resolved);
    }

    private sealed class StubConfiguration(EmailNotificationSettings settings) : IEmailNotificationConfigurationService
    {
        public Task<EmailNotificationSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(settings);
        public Task SaveSettingsAsync(SaveEmailNotificationSettingsRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<EmailDeliveryRecord>> GetDeliveryHistoryAsync(int count = 100, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task RetryAsync(Guid deliveryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
