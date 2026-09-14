namespace TeamHub.AIReports.Contracts;

public sealed class AiReportOptions
{
    public const string SectionName = "AIReports";

    public bool Enabled { get; set; }
    public string Provider { get; set; } = AiModelProviderKeys.Ollama;
    public string Endpoint { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "qwen3:8b";
    public double Temperature { get; set; }
    public int TimeoutSeconds { get; set; } = 180;
    public int MaximumConcurrentRequests { get; set; } = 1;
    public string CredentialEnvironmentVariable { get; set; } = "TEAMHUB_AI_PROVIDER_API_KEY";

    public AiProviderSettings ToProviderSettings() => new(
        Provider.Trim(),
        new Uri(Endpoint.Trim(), UriKind.Absolute),
        Model.Trim(),
        Temperature,
        TimeSpan.FromSeconds(TimeoutSeconds),
        CredentialEnvironmentVariable.Trim());
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
