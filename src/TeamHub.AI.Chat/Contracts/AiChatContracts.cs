using TeamHub.AI.Contracts;

namespace TeamHub.AI.Chat.Contracts;

public enum AiChatMode
{
    TeamHub,
    General
}

public static class AiChatExternalSources
{
    public const string Jira = "Jira";
    public const string Confluence = "Confluence";

    public static IReadOnlyList<string> All { get; } = [Jira, Confluence];
}

public sealed record AiChatUserContext(
    string UserId,
    string DisplayName,
    string UserType,
    IReadOnlySet<string>? ApprovedExternalSources = null)
{
    public bool HasApprovedExternalSource(string source) =>
        ApprovedExternalSources?.Any(item =>
            string.Equals(item, source, StringComparison.OrdinalIgnoreCase)) == true;
}

public sealed record AiChatMessage(string Role, string Content);

public sealed record AiChatDocument(
    string Name,
    string MediaType,
    string Content);

public sealed record AiChatRequest(
    AiChatMode Mode,
    string Model,
    IReadOnlyList<AiChatMessage> Messages,
    IReadOnlyList<AiChatDocument>? Documents = null);

public sealed record AiChatSource(
    string Id,
    string Title,
    string Content);

public sealed record AiChatCitation(string Id, string Title);

public sealed record AiChatResponse(
    string Answer,
    string Model,
    IReadOnlyList<AiChatCitation> Citations,
    string? RequestId = null);

public interface IAiChatService
{
    Task<IReadOnlyList<AiModelCatalogItem>> GetModelsAsync(
        CancellationToken cancellationToken = default);

    Task<AiChatResponse> SendAsync(
        AiChatUserContext user,
        AiChatRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Supplies already-authorized application knowledge. Implementations must apply
/// module and row-level authorization before returning any content.
/// </summary>
public interface IAiChatKnowledgeSource
{
    Task<IReadOnlyList<AiChatSource>> SearchAsync(
        AiChatUserContext user,
        string question,
        CancellationToken cancellationToken = default);
}

public sealed class AiChatOptions
{
    public const string SectionName = "AI:Chat";

    public int MaximumMessages { get; set; } = 20;
    public int MaximumMessageCharacters { get; set; } = 8_000;
    public int MaximumDocuments { get; set; } = 5;
    public int MaximumDocumentCharacters { get; set; } = 120_000;
    public int MaximumKnowledgeCharacters { get; set; } = 120_000;
    public int MaximumLocalPromptCharacters { get; set; } = 18_000;
    public int MaximumOutputTokens { get; set; } = 2_000;
}

public sealed class AiChatException(string message, Exception? innerException = null)
    : Exception(message, innerException);
