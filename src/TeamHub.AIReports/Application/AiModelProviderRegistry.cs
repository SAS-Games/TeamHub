using Microsoft.Extensions.Options;
using TeamHub.AIReports.Contracts;

namespace TeamHub.AIReports.Application;

internal sealed class AiModelProviderRegistry : IAiModelProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IAiModelProvider> providers;

    public AiModelProviderRegistry(IEnumerable<IAiModelProvider> providers)
    {
        this.providers = providers.ToDictionary(provider => provider.Key, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> ProviderKeys => providers.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();

    public IAiModelProvider GetRequired(string providerKey)
    {
        if (!string.IsNullOrWhiteSpace(providerKey)
            && providers.TryGetValue(providerKey.Trim(), out var provider))
        {
            return provider;
        }

        var available = ProviderKeys.Count == 0 ? "none" : string.Join(", ", ProviderKeys);
        throw new InvalidOperationException(
            $"AI model provider '{providerKey}' is not registered. Available providers: {available}.");
    }
}

internal sealed class AiReportModelService(
    IOptionsMonitor<AiReportOptions> options,
    IAiModelProviderRegistry providers) : IAiReportModelService
{
    public bool IsEnabled => options.CurrentValue.Enabled;

    public Task<AiProviderHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var current = GetEnabledOptions();
        return providers.GetRequired(current.Provider)
            .CheckHealthAsync(current.ToProviderSettings(), cancellationToken);
    }

    public Task<AiStructuredGenerationResult> GenerateStructuredAsync(
        AiStructuredGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = GetEnabledOptions();
        return providers.GetRequired(current.Provider)
            .GenerateStructuredAsync(current.ToProviderSettings(), request, cancellationToken);
    }

    private AiReportOptions GetEnabledOptions()
    {
        var current = options.CurrentValue;
        return current.Enabled
            ? current
            : throw new InvalidOperationException("AI Reports is disabled. Enable AIReports:Enabled before using the model runtime.");
    }
}
