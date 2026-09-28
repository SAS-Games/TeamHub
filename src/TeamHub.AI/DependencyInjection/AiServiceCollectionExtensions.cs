using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.AI.Application;
using TeamHub.AI.Contracts;
using TeamHub.AI.ModelProviders;

namespace TeamHub.AI;

public static class AiServiceCollectionExtensions
{
    internal const string HttpClientName = "TeamHub.AI";

    public static IServiceCollection AddTeamHubAi(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AiRuntimeOptions>()
            .Bind(configuration.GetSection(AiRuntimeOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.Provider),
                "AI:Provider is required.")
            .Validate(options => Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
                    && endpoint.Scheme is "http" or "https",
                "AI:Endpoint must be an absolute HTTP or HTTPS URL.")
            .Validate(options => !options.OfflineOnly || AiRuntimeOptions.IsLoopbackEndpoint(options.Endpoint),
                "AI:Endpoint must use localhost or another loopback address when AI:OfflineOnly is enabled.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Model),
                "AI:Model is required.")
            .Validate(options => options.Temperature is >= 0 and <= 2,
                "AI:Temperature must be between 0 and 2.")
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 1800,
                "AI:TimeoutSeconds must be between 1 and 1800.")
            .ValidateOnStart();

        services.AddHttpClient(HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddSingleton<IAiProviderCredentialAccessor, EnvironmentAiProviderCredentialAccessor>();
        services.AddSingleton<IAiModelProvider, OllamaModelProvider>();
        services.AddSingleton<IAiModelProvider, OpenAiCompatibleModelProvider>();
        services.AddSingleton<IAiModelProviderRegistry, AiModelProviderRegistry>();
        services.AddSingleton<IAiModelService, AiModelService>();

        return services;
    }
}
