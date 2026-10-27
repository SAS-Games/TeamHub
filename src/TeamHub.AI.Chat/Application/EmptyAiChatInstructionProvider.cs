using TeamHub.AI.Chat.Contracts;

namespace TeamHub.AI.Chat.Application;

internal sealed class EmptyAiChatInstructionProvider : IAiChatInstructionProvider
{
    public Task<string> GetTeamHubInstructionsAsync(
        string question,
        IReadOnlyList<AiChatSource> sources,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);
}
