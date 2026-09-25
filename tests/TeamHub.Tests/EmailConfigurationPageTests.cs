using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using TeamHub.Application.Interfaces;
using TeamHub.Domain.Enums;
using TeamHub.Web.Pages.Configuration;

namespace TeamHub.Tests;

public sealed class EmailConfigurationPageTests
{
    [Fact]
    public async Task OnGetAsync_LoadsPersistedNonSecretSettings()
    {
        var saved = new EmailNotificationSettings
        {
            Enabled = true,
            Host = "smtp.saved.example",
            Port = 2525,
            UseAuthentication = true,
            Username = "saved-user",
            FromAddress = "teamhub@example.com",
            HasPassword = true,
            DefaultCc = "cc@example.com",
            CompletionRecipient = "workflow-owner@example.com",
            MaxDeliveryAttempts = 7,
            RetryDelayMinutes = 15
        };
        var configuration = new StubEmailConfigurationService(saved);
        var model = new EmailModel(configuration, new StubNotificationService());

        await model.OnGetAsync(CancellationToken.None);

        model.Settings.Host.Should().Be("smtp.saved.example");
        model.Settings.Port.Should().Be(2525);
        model.Settings.UseAuthentication.Should().BeTrue();
        model.Settings.Username.Should().Be("saved-user");
        model.Settings.FromAddress.Should().Be("teamhub@example.com");
        model.Settings.HasPassword.Should().BeTrue();
        model.Settings.DefaultCc.Should().Be("cc@example.com");
        model.Settings.CompletionRecipient.Should().Be("workflow-owner@example.com");
        model.Settings.MaxDeliveryAttempts.Should().Be(7);
        model.Settings.RetryDelayMinutes.Should().Be(15);
    }

    [Fact]
    public async Task OnPostSaveAsync_AllowsBlankOptionalDeliveryDefaults()
    {
        var configuration = new StubEmailConfigurationService(new EmailNotificationSettings());
        var model = new EmailModel(configuration, new StubNotificationService())
        {
            Settings = new EmailNotificationSettingsInput
            {
                Enabled = false,
                Host = null,
                Port = null,
                Username = null,
                FromAddress = null,
                DefaultCc = null,
                DefaultBcc = null,
                CompletionRecipient = null,
                MaxDeliveryAttempts = null,
                RetryDelayMinutes = null,
                AssignmentSubjectTemplate = null,
                AssignmentBodyTemplate = null,
                ReminderSubjectTemplate = null,
                ReminderBodyTemplate = null,
                EscalationSubjectTemplate = null,
                EscalationBodyTemplate = null,
                CompletionSubjectTemplate = null,
                CompletionBodyTemplate = null
            }
        };

        var result = await model.OnPostSaveAsync(CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>();
        configuration.SavedRequest.Should().NotBeNull();
        configuration.SavedRequest!.Settings.DefaultCc.Should().BeEmpty();
        configuration.SavedRequest.Settings.DefaultBcc.Should().BeEmpty();
        configuration.SavedRequest.Settings.CompletionRecipient.Should().BeEmpty();
        configuration.SavedRequest.Settings.MaxDeliveryAttempts.Should().Be(3);
        configuration.SavedRequest.Settings.RetryDelayMinutes.Should().Be(5);
    }

    private sealed class StubEmailConfigurationService(EmailNotificationSettings settings)
        : IEmailNotificationConfigurationService
    {
        public SaveEmailNotificationSettingsRequest? SavedRequest { get; private set; }

        public Task<EmailNotificationSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(settings);

        public Task SaveSettingsAsync(
            SaveEmailNotificationSettingsRequest request,
            CancellationToken cancellationToken = default)
        {
            SavedRequest = request;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EmailDeliveryRecord>> GetDeliveryHistoryAsync(
            int count = 100,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EmailDeliveryRecord>>([]);

        public Task RetryAsync(Guid deliveryId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubNotificationService : INotificationService
    {
        public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
