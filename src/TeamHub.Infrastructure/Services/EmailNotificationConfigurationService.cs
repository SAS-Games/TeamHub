using System.Net.Mail;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TeamHub.Application.Interfaces;
using TeamHub.Domain.Entities;
using TeamHub.Infrastructure.Persistence;

namespace TeamHub.Infrastructure.Services;

internal interface IEmailCredentialAccessor
{
    Task<(EmailNotificationSettings Settings, string Password)> GetDeliverySettingsAsync(CancellationToken cancellationToken = default);
}

internal sealed class EmailNotificationConfigurationService :
    IEmailNotificationConfigurationService,
    IEmailCredentialAccessor
{
    private const string ProtectorPurpose = "TeamHub.WorkCenter.EmailPassword.v1";
    private readonly WorkflowDbContext dbContext;
    private readonly IDataProtector protector;

    public EmailNotificationConfigurationService(
        WorkflowDbContext dbContext,
        IDataProtectionProvider dataProtectionProvider)
    {
        this.dbContext = dbContext;
        protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    }

    public async Task<EmailNotificationSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var record = await GetRecordAsync(cancellationToken);
        return ToSettings(record);
    }

    public async Task SaveSettingsAsync(
        SaveEmailNotificationSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Settings);
        var input = request.Settings;
        var record = await GetRecordAsync(cancellationToken);

        var host = input.Host?.Trim() ?? string.Empty;
        var username = input.Username?.Trim() ?? string.Empty;
        var fromAddress = input.FromAddress?.Trim() ?? string.Empty;
        var passwordWillExist = !request.RemovePassword
            && (!string.IsNullOrWhiteSpace(request.Password) || !string.IsNullOrWhiteSpace(record.PasswordProtected));
        if (input.Enabled)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("SMTP host is required when email is enabled.");
            if (input.Port is < 1 or > 65535) throw new ArgumentException("SMTP port must be between 1 and 65535.");
            if (!IsEmail(fromAddress)) throw new ArgumentException("A valid From address is required when email is enabled.");
            if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("SMTP username is required when email is enabled.");
            if (!passwordWillExist) throw new ArgumentException("SMTP password is required when email is enabled.");
        }

        ValidateEmailList(input.DefaultCc, "Default CC");
        ValidateEmailList(input.DefaultBcc, "Default BCC");
        record.Enabled = input.Enabled;
        record.Host = host;
        record.Port = Math.Clamp(input.Port, 1, 65535);
        record.Username = username;
        record.FromAddress = fromAddress;
        record.UseSsl = input.UseSsl;
        record.DefaultCc = input.DefaultCc?.Trim() ?? string.Empty;
        record.DefaultBcc = input.DefaultBcc?.Trim() ?? string.Empty;
        record.CompletionRecipient = input.CompletionRecipient?.Trim() ?? string.Empty;
        record.MaxDeliveryAttempts = Math.Clamp(input.MaxDeliveryAttempts, 1, 10);
        record.RetryDelayMinutes = Math.Clamp(input.RetryDelayMinutes, 1, 1440);
        record.AssignmentSubjectTemplate = Template(input.AssignmentSubjectTemplate, "{Subject}");
        record.AssignmentBodyTemplate = Template(input.AssignmentBodyTemplate, "{Body}");
        record.ReminderSubjectTemplate = Template(input.ReminderSubjectTemplate, "{Subject}");
        record.ReminderBodyTemplate = Template(input.ReminderBodyTemplate, "{Body}");
        record.EscalationSubjectTemplate = Template(input.EscalationSubjectTemplate, "{Subject}");
        record.EscalationBodyTemplate = Template(input.EscalationBodyTemplate, "{Body}");
        record.CompletionSubjectTemplate = Template(input.CompletionSubjectTemplate, "{Subject}");
        record.CompletionBodyTemplate = Template(input.CompletionBodyTemplate, "{Body}");
        if (request.RemovePassword) record.PasswordProtected = null;
        else if (!string.IsNullOrWhiteSpace(request.Password)) record.PasswordProtected = protector.Protect(request.Password);
        record.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EmailDeliveryRecord>> GetDeliveryHistoryAsync(
        int count = 100,
        CancellationToken cancellationToken = default) =>
        await dbContext.NotificationOutbox.AsNoTracking()
            .OrderByDescending(item => item.CreatedAtUtc)
            .Take(Math.Clamp(count, 1, 500))
            .Select(item => new EmailDeliveryRecord(
                item.Id, item.CreatedAtUtc, item.SentAtUtc, item.Type, item.Recipient,
                item.Subject, item.Status, item.Attempts, item.LastError))
            .ToListAsync(cancellationToken);

    public async Task RetryAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        var item = await dbContext.NotificationOutbox.SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken);
        if (item is null) return;
        item.Status = "Pending";
        item.Attempts = 0;
        item.NextAttemptAtUtc = DateTime.UtcNow;
        item.SentAtUtc = null;
        item.LastError = null;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    async Task<(EmailNotificationSettings Settings, string Password)> IEmailCredentialAccessor.GetDeliverySettingsAsync(
        CancellationToken cancellationToken)
    {
        var record = await GetRecordAsync(cancellationToken);
        var password = string.IsNullOrWhiteSpace(record.PasswordProtected)
            ? string.Empty
            : protector.Unprotect(record.PasswordProtected);
        return (ToSettings(record), password);
    }

    private async Task<EmailNotificationSettingsRecord> GetRecordAsync(CancellationToken cancellationToken)
    {
        var record = await dbContext.EmailNotificationSettings.SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        if (record is not null) return record;
        record = new EmailNotificationSettingsRecord { Id = 1, UpdatedAtUtc = DateTime.UtcNow };
        dbContext.EmailNotificationSettings.Add(record);
        await dbContext.SaveChangesAsync(cancellationToken);
        return record;
    }

    private static EmailNotificationSettings ToSettings(EmailNotificationSettingsRecord record) => new()
    {
        Enabled = record.Enabled,
        Host = record.Host,
        Port = record.Port,
        Username = record.Username,
        FromAddress = record.FromAddress,
        UseSsl = record.UseSsl,
        HasPassword = !string.IsNullOrWhiteSpace(record.PasswordProtected),
        DefaultCc = record.DefaultCc,
        DefaultBcc = record.DefaultBcc,
        CompletionRecipient = record.CompletionRecipient,
        MaxDeliveryAttempts = record.MaxDeliveryAttempts,
        RetryDelayMinutes = record.RetryDelayMinutes,
        AssignmentSubjectTemplate = record.AssignmentSubjectTemplate,
        AssignmentBodyTemplate = record.AssignmentBodyTemplate,
        ReminderSubjectTemplate = record.ReminderSubjectTemplate,
        ReminderBodyTemplate = record.ReminderBodyTemplate,
        EscalationSubjectTemplate = record.EscalationSubjectTemplate,
        EscalationBodyTemplate = record.EscalationBodyTemplate,
        CompletionSubjectTemplate = record.CompletionSubjectTemplate,
        CompletionBodyTemplate = record.CompletionBodyTemplate
    };

    private static string Template(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static bool IsEmail(string value) => MailAddress.TryCreate(value, out _);

    private static void ValidateEmailList(string? value, string label)
    {
        foreach (var address in SplitAddresses(value))
        {
            if (!IsEmail(address)) throw new ArgumentException($"{label} contains an invalid email address: {address}");
        }
    }

    internal static IReadOnlyList<string> SplitAddresses(string? value) =>
        (value ?? string.Empty)
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

internal sealed class PassthroughNotificationRecipientResolver : INotificationRecipientResolver
{
    public Task<string?> ResolveEmailAsync(string recipient, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(recipient?.Trim());
}
