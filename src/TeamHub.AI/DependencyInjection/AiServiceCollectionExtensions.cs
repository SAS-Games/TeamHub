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
        services.AddOptions<AiChatOptions>()
            .Bind(configuration.GetSection(AiChatOptions.SectionName))
            .Validate(options => options.MaximumMessages is >= 1 and <= 100,
                "AI:Chat:MaximumMessages must be between 1 and 100.")
            .Validate(options => options.MaximumMessageCharacters is >= 100 and <= 100_000,
                "AI:Chat:MaximumMessageCharacters must be between 100 and 100,000.")
            .Validate(options => options.MaximumDocuments is >= 0 and <= 20,
                "AI:Chat:MaximumDocuments must be between 0 and 20.")
            .Validate(options => options.MaximumDocumentCharacters is >= 1_000 and <= 1_000_000,
                "AI:Chat:MaximumDocumentCharacters must be between 1,000 and 1,000,000.")
            .Validate(options => options.MaximumKnowledgeCharacters is >= 1_000 and <= 1_000_000,
                "AI:Chat:MaximumKnowledgeCharacters must be between 1,000 and 1,000,000.")
            .Validate(options => options.MaximumLocalPromptCharacters is >= 2_000 and <= 200_000,
                "AI:Chat:MaximumLocalPromptCharacters must be between 2,000 and 200,000.")
            .Validate(options => options.MaximumInstructionCharacters is >= 1_000 and <= 200_000,
                "AI:Chat:MaximumInstructionCharacters must be between 1,000 and 200,000.")
            .Validate(options => options.MaximumOutputTokens is >= 128 and <= 32_000,
                "AI:Chat:MaximumOutputTokens must be between 128 and 32,000.")
            .Validate(options => !options.MeetingNotes.Enabled
                    || !string.IsNullOrWhiteSpace(options.MeetingNotes.InstructionPath),
                "AI:Chat:MeetingNotes:InstructionPath is required when Meeting Notes mode is enabled.")
            .ValidateOnStart();
        services.AddOptions<AiReportGeneratorOptions>()
            .Bind(configuration.GetSection(AiReportGeneratorOptions.SectionName));
        services.TryAddScoped<IAiConfigurationService, OptionsAiConfigurationService>();

        return services;
    }
}
