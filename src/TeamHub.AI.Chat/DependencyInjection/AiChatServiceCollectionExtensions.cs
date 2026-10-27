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
        services.TryAddSingleton<IAiChatInstructionProvider, EmptyAiChatInstructionProvider>();
        services.AddScoped<IAiChatService, AiChatService>();
        return services;
    }
}
