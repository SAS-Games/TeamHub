using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TeamHub.AI.Chat.Application;
using TeamHub.AI.Chat.Contracts;

namespace TeamHub.AI.Chat;

public static class AiChatServiceCollectionExtensions
{
    public static IServiceCollection AddAiChat(
        this IServiceCollection services,
        IConfiguration configuration)
    {
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
            .Validate(options => options.MaximumInstructionCharacters is >= 1_000 and <= 200_000,
                "AI:Chat:MaximumInstructionCharacters must be between 1,000 and 200,000.")
            .Validate(options => options.MaximumOutputTokens is >= 128 and <= 32_000,
                "AI:Chat:MaximumOutputTokens must be between 128 and 32,000.")
            .ValidateOnStart();
        services.TryAddSingleton<IAiChatInstructionProvider, EmptyAiChatInstructionProvider>();
        services.AddScoped<IAiChatService, AiChatService>();
        return services;
    }
}
