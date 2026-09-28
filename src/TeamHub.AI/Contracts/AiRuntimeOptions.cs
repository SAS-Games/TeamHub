namespace TeamHub.AI.Contracts;

public sealed class AiRuntimeOptions
{
    public const string SectionName = "AI";

    public string Provider { get; set; } = AiModelProviderKeys.Ollama;
    public string Endpoint { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "qwen3:8b";
    public bool OfflineOnly { get; set; } = true;
    public double Temperature { get; set; }
    public int TimeoutSeconds { get; set; } = 180;
    public string CredentialEnvironmentVariable { get; set; } = "TEAMHUB_AI_PROVIDER_API_KEY";

    public AiProviderSettings ToProviderSettings()
    {
        var endpoint = new Uri(Endpoint.Trim(), UriKind.Absolute);
        if (OfflineOnly && !endpoint.IsLoopback)
        {
            throw new InvalidOperationException(
                "AI offline-only mode requires a loopback endpoint such as http://127.0.0.1 or http://localhost.");
        }

        return new AiProviderSettings(
            Provider.Trim(),
            endpoint,
            Model.Trim(),
            Temperature,
            TimeSpan.FromSeconds(TimeoutSeconds),
            CredentialEnvironmentVariable.Trim());
    }

    internal static bool IsLoopbackEndpoint(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var endpoint)
        && endpoint.Scheme is "http" or "https"
        && endpoint.IsLoopback;
}

public static class AiModelProviderKeys
{
    public const string Ollama = "Ollama";
    public const string OpenAiCompatible = "OpenAICompatible";
}

public sealed record AiProviderSettings(
    string Provider,
    Uri Endpoint,
    string Model,
    double Temperature,
    TimeSpan Timeout,
    string CredentialEnvironmentVariable);
