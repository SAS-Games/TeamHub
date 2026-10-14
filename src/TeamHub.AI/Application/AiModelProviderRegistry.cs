using Microsoft.Extensions.Options;
using TeamHub.AI.Contracts;

namespace TeamHub.AI.Application;

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

internal sealed class AiModelService(
    IAiConfigurationService configuration,
    IAiModelProviderRegistry providers) : IAiModelService
{
    public async Task<AiProviderHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var current = (await configuration.GetAsync(cancellationToken)).Runtime;
        return await providers.GetRequired(current.Provider)
            .CheckHealthAsync(current.ToProviderSettings(), cancellationToken);
    }

    public async Task<AiProviderHealthResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var current = (await configuration.GetAsync(cancellationToken)).Runtime;
        return await providers.GetRequired(current.Provider)
            .TestConnectionAsync(current.ToProviderSettings(), cancellationToken);
    }

    public async Task<AiStructuredGenerationResult> GenerateStructuredAsync(
        AiStructuredGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = (await configuration.GetAsync(cancellationToken)).Runtime;
        return await providers.GetRequired(current.Provider)
            .GenerateStructuredAsync(current.ToProviderSettings(), request, cancellationToken);
    }
}
