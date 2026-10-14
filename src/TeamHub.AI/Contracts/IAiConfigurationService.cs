namespace TeamHub.AI.Contracts;

public interface IAiConfigurationService : IAiProviderCredentialAccessor
{
    Task<AiConfigurationSnapshot> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        SaveAiConfigurationRequest request,
        CancellationToken cancellationToken = default);
}
