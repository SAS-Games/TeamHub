using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamHub.AI.Contracts;
using TeamHub.AI.ModelProviders;
using TeamHub.Domain.Entities;
using TeamHub.Infrastructure.Persistence;

namespace TeamHub.Infrastructure.Services;

internal sealed class AiConfigurationService : IAiConfigurationService
{
    private const string ProtectorPurpose = "TeamHub.AI.ProviderCredential.v1";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly WorkflowDbContext dbContext;
    private readonly IDataProtector protector;
    private readonly IOptionsMonitor<AiRuntimeOptions> runtimeDefaults;
    private readonly IOptionsMonitor<AiFlowDesignerOptions> flowDefaults;
    private readonly IOptionsMonitor<AiReportGeneratorOptions> reportDefaults;

    public AiConfigurationService(
        WorkflowDbContext dbContext,
        IDataProtectionProvider dataProtectionProvider,
        IOptionsMonitor<AiRuntimeOptions> runtimeDefaults,
        IOptionsMonitor<AiFlowDesignerOptions> flowDefaults,
        IOptionsMonitor<AiReportGeneratorOptions> reportDefaults)
    {
        this.dbContext = dbContext;
        protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        this.runtimeDefaults = runtimeDefaults;
        this.flowDefaults = flowDefaults;
        this.reportDefaults = reportDefaults;
    }

    public async Task<AiConfigurationSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var record = await dbContext.AiConfiguration.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        var settings = record is null
            ? CreateDefaults()
            : Deserialize(record.SettingsJson);

        settings.HasCredential = !string.IsNullOrWhiteSpace(record?.CredentialProtected)
            || HasEnvironmentCredential(settings.Runtime);
        settings.UpdatedAtUtc = record?.UpdatedAtUtc;
        settings.UpdatedBy = record?.UpdatedBy;
        return settings;
    }

    public async Task SaveAsync(
        SaveAiConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var record = await dbContext.AiConfiguration
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        if (record is null)
        {
            record = new AiConfigurationRecord { Id = 1 };
            dbContext.AiConfiguration.Add(record);
        }

        var snapshot = new AiConfigurationSnapshot
        {
            Runtime = request.Runtime,
            FlowDesigner = request.FlowDesigner,
            ReportGenerator = request.ReportGenerator
        };
        record.SettingsJson = JsonSerializer.Serialize(snapshot, SerializerOptions);
        if (request.RemoveCredential)
        {
            record.CredentialProtected = null;
        }
        else if (!string.IsNullOrWhiteSpace(request.Credential))
        {
            record.CredentialProtected = protector.Protect(request.Credential.Trim());
        }
        record.UpdatedAtUtc = DateTime.UtcNow;
        record.UpdatedBy = request.UpdatedBy.Trim();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<string?> GetCredentialAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default)
    {
        var protectedCredential = await dbContext.AiConfiguration.AsNoTracking()
            .Where(item => item.Id == 1)
            .Select(item => item.CredentialProtected)
            .SingleOrDefaultAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(protectedCredential))
        {
            try
            {
                return protector.Unprotect(protectedCredential);
            }
            catch (CryptographicException exception)
            {
                throw new InvalidOperationException(
                    "The stored AI provider credential cannot be decrypted. Ask an administrator to re-enter it.",
                    exception);
            }
        }

        return string.IsNullOrWhiteSpace(settings.CredentialEnvironmentVariable)
            ? null
            : Environment.GetEnvironmentVariable(settings.CredentialEnvironmentVariable);
    }

    private AiConfigurationSnapshot CreateDefaults() => new()
    {
        Runtime = Clone(runtimeDefaults.CurrentValue),
        FlowDesigner = Clone(flowDefaults.CurrentValue),
        ReportGenerator = Clone(reportDefaults.CurrentValue)
    };

    private static AiConfigurationSnapshot Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AiConfigurationSnapshot>(json, SerializerOptions)
                ?? throw new InvalidOperationException("The saved AI configuration is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The saved AI configuration is invalid.", exception);
        }
    }

    private static T Clone<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, SerializerOptions), SerializerOptions)!;

    private static bool HasEnvironmentCredential(AiRuntimeOptions runtime)
    {
        var name = runtime.GetSelectedCredentialEnvironmentVariable();
        return !string.IsNullOrWhiteSpace(name)
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));
    }

    private static void Validate(SaveAiConfigurationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UpdatedBy))
            throw new ArgumentException("The administrator identity is required.");
        if (request.Runtime.Provider is not (AiModelProviderKeys.Ollama
            or AiModelProviderKeys.LlamaCpp
            or AiModelProviderKeys.OpenAiCompatible
            or AiModelProviderKeys.PfbAiGateway))
            throw new ArgumentException("Select a supported AI provider.");

        try
        {
            _ = request.Runtime.ToProviderSettings();
        }
        catch (Exception exception) when (exception is UriFormatException or InvalidOperationException)
        {
            throw new ArgumentException(exception.Message, exception);
        }

        if (string.Equals(request.Runtime.Provider, AiModelProviderKeys.PfbAiGateway, StringComparison.Ordinal)
            && request.Runtime.GetSelectedApiMode() is not (
                PfbAiGatewayModelProvider.CommonChatMode
                or PfbAiGatewayModelProvider.OpenAiChatMode
                or PfbAiGatewayModelProvider.OpenAiResponsesMode))
            throw new ArgumentException("Select a supported PFB AI Gateway API route.");

        if (request.Runtime.Temperature is < 0 or > 2)
            throw new ArgumentException("Temperature must be between 0 and 2.");
        if (request.Runtime.TimeoutSeconds is < 1 or > 1800)
            throw new ArgumentException("Timeout must be between 1 and 1,800 seconds.");
        if (request.FlowDesigner.MaximumSourceDocuments is < 1 or > 50)
            throw new ArgumentException("Maximum source documents must be between 1 and 50.");
        if (request.FlowDesigner.MaximumSourceCharacters is < 1_000 or > 2_000_000)
            throw new ArgumentException("Maximum source characters must be between 1,000 and 2,000,000.");
        if (request.FlowDesigner.MaximumPromptCharacters is < 100 or > 20_000)
            throw new ArgumentException("Maximum prompt characters must be between 100 and 20,000.");
        if (request.FlowDesigner.MaximumOutputTokens is < 1_000 or > 32_000)
            throw new ArgumentException("Maximum output tokens must be between 1,000 and 32,000.");
        if (request.FlowDesigner.MaximumDiagrams is < 1 or > 25)
            throw new ArgumentException("Maximum diagrams must be between 1 and 25.");
        if (request.FlowDesigner.MaximumHierarchyDepth is < 1 or > 6)
            throw new ArgumentException("Maximum hierarchy depth must be between 1 and 6.");
        if (request.ReportGenerator.MaximumConcurrentRequests is < 1 or > 16)
            throw new ArgumentException("Maximum concurrent report requests must be between 1 and 16.");
    }
}
