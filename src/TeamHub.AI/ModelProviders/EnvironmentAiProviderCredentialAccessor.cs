using TeamHub.AI.Contracts;

namespace TeamHub.AI.ModelProviders;

internal sealed class EnvironmentAiProviderCredentialAccessor : IAiProviderCredentialAccessor
{
    public ValueTask<string?> GetCredentialAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(settings.CredentialEnvironmentVariable))
        {
            return ValueTask.FromResult<string?>(null);
        }

        return ValueTask.FromResult(Environment.GetEnvironmentVariable(settings.CredentialEnvironmentVariable));
    }
}
