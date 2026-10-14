using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        services.AddTeamHubAiConfiguration(configuration);

        services.AddHttpClient(HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.TryAddScoped<IAiProviderCredentialAccessor>(provider =>
            provider.GetRequiredService<IAiConfigurationService>());
        services.AddScoped<IAiModelProvider, OllamaModelProvider>();
        services.AddScoped<IAiModelProvider, LlamaCppModelProvider>();
        services.AddScoped<IAiModelProvider, OpenAiCompatibleModelProvider>();
        services.AddScoped<IAiModelProvider, PfbAiGatewayModelProvider>();
        services.AddScoped<IAiModelProviderRegistry, AiModelProviderRegistry>();
        services.AddScoped<IAiModelService, AiModelService>();

        return services;
    }

    public static IServiceCollection AddTeamHubAiConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AiRuntimeOptions>()
            .Bind(configuration.GetSection(AiRuntimeOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.Provider),
                "AI:Provider is required.")
            .Validate(options => Uri.TryCreate(options.GetSelectedEndpoint(), UriKind.Absolute, out var endpoint)
                    && endpoint.Scheme is "http" or "https",
                "The selected AI provider endpoint must be an absolute HTTP or HTTPS URL.")
            .Validate(options => !options.OfflineOnly
                    || AiRuntimeOptions.IsLoopbackEndpoint(options.GetSelectedEndpoint()),
                "The selected AI provider endpoint must use localhost or another loopback address when AI:OfflineOnly is enabled.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.GetSelectedModel()),
                "The selected AI provider model is required.")
            .Validate(options => options.Temperature is >= 0 and <= 2,
                "AI:Temperature must be between 0 and 2.")
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 1800,
                "AI:TimeoutSeconds must be between 1 and 1800.")
            .ValidateOnStart();

        services.AddOptions<AiFlowDesignerOptions>()
            .Bind(configuration.GetSection(AiFlowDesignerOptions.SectionName));
        services.AddOptions<AiReportGeneratorOptions>()
            .Bind(configuration.GetSection(AiReportGeneratorOptions.SectionName));
        services.TryAddScoped<IAiConfigurationService, OptionsAiConfigurationService>();

        return services;
    }
}
