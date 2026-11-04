using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TeamHub.AI.Chat.Application;
using TeamHub.AI.Chat.Contracts;
using TeamHub.AI.Usage;

namespace TeamHub.AI.Chat;

public static class AiChatServiceCollectionExtensions
{
    public static IServiceCollection AddAiChat(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAiUsageTrackingFallback();
        services.TryAddSingleton<IAiChatInstructionProvider, EmptyAiChatInstructionProvider>();
        services.TryAddSingleton<IAiMeetingNotesInstructionProvider, EmptyAiMeetingNotesInstructionProvider>();
        services.AddScoped<IAiChatService, AiChatService>();
        return services;
    }
}
