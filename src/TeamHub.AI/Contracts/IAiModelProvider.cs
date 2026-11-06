using System.Text.Json;

namespace TeamHub.AI.Contracts;

public interface IAiModelProvider
{
    string Key { get; }

    Task<AiProviderHealthResult> CheckHealthAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default);

    Task<AiProviderHealthResult> TestConnectionAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default) =>
        CheckHealthAsync(settings, cancellationToken);

    Task<AiStructuredGenerationResult> GenerateStructuredAsync(
        AiProviderSettings settings,
        AiStructuredGenerationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IAiModelCatalogProvider
{
    Task<IReadOnlyList<AiModelCatalogItem>> GetModelsAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default);
}

public interface IAiModelProviderRegistry
{
    IAiModelProvider GetRequired(string providerKey);
    IReadOnlyList<string> ProviderKeys { get; }
}

public interface IAiModelService
{
    Task<AiProviderHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    Task<AiProviderHealthResult> TestConnectionAsync(CancellationToken cancellationToken = default) =>
        CheckHealthAsync(cancellationToken);
    Task<AiStructuredGenerationResult> GenerateStructuredAsync(
        AiStructuredGenerationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IAiProviderCredentialAccessor
{
    ValueTask<string?> GetCredentialAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default);

    async ValueTask<AiProviderCredential?> ResolveCredentialAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default)
    {
        var value = await GetCredentialAsync(settings, cancellationToken);
        return string.IsNullOrWhiteSpace(value)
            ? null
            : new AiProviderCredential(value, AiProviderCredentialSource.Unknown);
    }
}

public enum AiProviderCredentialSource
{
    Unknown,
    Personal,
    MachineToMachine
}

public sealed class AiProviderCredential(string value, AiProviderCredentialSource source)
{
    public string Value { get; } = value;
    public AiProviderCredentialSource Source { get; } = source;

    public override string ToString() => Source.ToString();
}

public sealed record AiProviderHealthResult(
    bool IsAvailable,
    string Provider,
    string Model,
    bool IsModelAvailable,
    string Message);

public sealed record AiModelCatalogItem(
    string Id,
    string ModelType,
    string CostTier,
    IReadOnlyList<string> Endpoints);

public sealed record AiStructuredGenerationRequest(
    string SystemPrompt,
    string UserPrompt,
    JsonElement ResponseSchema,
    int? MaximumOutputTokens = null,
    IReadOnlyList<AiImageInput>? Images = null);

public sealed record AiImageInput(
    string Name,
    string MediaType,
    string Base64Data);

public sealed record AiStructuredGenerationResult(
    string Provider,
    string Model,
    string Content,
    string? RequestId = null,
    AiTokenUsage? Usage = null,
    AiUsageDiagnostics? UsageDiagnostics = null);

public sealed record AiTokenUsage(
    int InputTokens,
    int OutputTokens,
    int TotalTokens);

public sealed record AiUsageDiagnostics(
    string Summary,
    int HttpStatusCode,
    string ApiMode,
    string CredentialSource,
    string ResponseShape,
    string? ProviderRequestId);

public sealed class AiModelProviderException : Exception
{
    public AiModelProviderException(string message) : base(message)
    {
    }

    public AiModelProviderException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
