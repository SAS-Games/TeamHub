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
    IReadOnlyList<AiChatDocument>? Documents = null,
    IReadOnlyList<AiImageInput>? Images = null);

public sealed record AiChatSource(
    string Id,
    string Title,
    string Content);

public sealed record AiChatCitation(string Id, string Title);

public sealed record AiChatResponse(
    string Answer,
    string Model,
    IReadOnlyList<AiChatCitation> Citations,
    string? RequestId = null,
    AiTokenUsage? Usage = null);

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

/// <summary>
/// Supplies trusted, application-owned instructions that describe how TeamHub
/// knowledge should be routed and presented. This is never populated from a
/// user attachment or from retrieved page content.
/// </summary>
public interface IAiChatInstructionProvider
{
    Task<string> GetTeamHubInstructionsAsync(
        string question,
        IReadOnlyList<AiChatSource> sources,
        CancellationToken cancellationToken = default);
}

public sealed class AiChatException(string message, Exception? innerException = null)
    : Exception(message, innerException);
