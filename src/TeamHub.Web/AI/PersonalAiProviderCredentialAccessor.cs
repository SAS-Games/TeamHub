using Microsoft.AspNetCore.Http;
using TeamHub.AI.Contracts;
using TeamHub.Studio;

namespace TeamHub.Web.AI;

public sealed class PersonalAiProviderCredentialAccessor(
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
        if (user?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException(
                "Sign in and connect your Sony PFB AI Gateway token in My Connections.");

        var token = await personalCredentials.GetTokenAsync(userId, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "Your Sony PFB AI Gateway token is not connected. Add your API or bearer token in My Connections.");

        return token;
    }
}
