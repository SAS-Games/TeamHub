namespace TeamHub.AI.Contracts;

public interface IAiConfigurationService : IAiProviderCredentialAccessor
{
    Task<AiConfigurationSnapshot> GetAsync(CancellationToken cancellationToken = default);

    ValueTask<string?> GetPfbM2mCredentialAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<string?>(null);

    Task SaveAsync(
        SaveAiConfigurationRequest request,
        CancellationToken cancellationToken = default);
}
