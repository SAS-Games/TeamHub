using Microsoft.AspNetCore.Http;
using TeamHub.AI.Contracts;
using TeamHub.Studio;

namespace TeamHub.Web.AI;

public sealed class TeamHubAiProviderCredentialAccessor(
    IHttpContextAccessor httpContextAccessor,
    IPfbAiGatewayUserCredentialService personalCredentials,
    IAiConfigurationService configuration) : IAiProviderCredentialAccessor
{
    public async ValueTask<string?> GetCredentialAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(settings.Provider, AiModelProviderKeys.PfbAiGateway, StringComparison.OrdinalIgnoreCase))
            return await configuration.GetCredentialAsync(settings, cancellationToken);

        var user = httpContextAccessor.HttpContext?.User;
        var userId = user?.Identity?.Name;
        if (user?.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(userId))
        {
            var personalToken = await personalCredentials.GetTokenAsync(userId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(personalToken)) return personalToken;
        }

        var m2mCredential = await configuration.GetPfbM2mCredentialAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(m2mCredential)) return m2mCredential;

        throw new InvalidOperationException(
            "No Sony PFB AI Gateway credential is available. Connect a personal gateway token or ask an administrator to configure the PFB M2M application key in AI Services.");
    }
}
