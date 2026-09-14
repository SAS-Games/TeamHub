using System.Text.Json;

namespace TeamHub.AIReports.Contracts;

public interface IAiModelProvider
{
    string Key { get; }

    Task<AiProviderHealthResult> CheckHealthAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default);

    Task<AiStructuredGenerationResult> GenerateStructuredAsync(
        AiProviderSettings settings,
        AiStructuredGenerationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IAiModelProviderRegistry
{
    IAiModelProvider GetRequired(string providerKey);
    IReadOnlyList<string> ProviderKeys { get; }
}

public interface IAiReportModelService
{
    bool IsEnabled { get; }
    Task<AiProviderHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    Task<AiStructuredGenerationResult> GenerateStructuredAsync(
        AiStructuredGenerationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IAiProviderCredentialAccessor
{
    ValueTask<string?> GetCredentialAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default);
}

public sealed record AiProviderHealthResult(
    bool IsAvailable,
    string Provider,
    string Model,
    bool IsModelAvailable,
    string Message);

public sealed record AiStructuredGenerationRequest(
    string SystemPrompt,
    string UserPrompt,
    JsonElement ResponseSchema,
    int? MaximumOutputTokens = null);

public sealed record AiStructuredGenerationResult(
    string Provider,
    string Model,
    string Content,
    string? RequestId = null);

public sealed class AiModelProviderException : Exception
{
    public AiModelProviderException(string message) : base(message)
    {
    }

    public AiModelProviderException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
