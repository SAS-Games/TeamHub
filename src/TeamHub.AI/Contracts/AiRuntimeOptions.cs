using System.Text.Json.Serialization;

namespace TeamHub.AI.Contracts;

public sealed class AiRuntimeOptions
{
    public const string SectionName = "AI";

    public string Provider { get; set; } = AiModelProviderKeys.Ollama;
    public string Endpoint { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "qwen3:8b";
    public string ApiMode { get; set; } = string.Empty;
    [Obsolete("Use AiConfigurationSnapshot.Chat.Enabled.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ChatEnabled { get; set; }
    public Dictionary<string, AiProviderProfileOptions> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public bool OfflineOnly { get; set; } = true;
    public double Temperature { get; set; }
    public int TimeoutSeconds { get; set; } = 180;
    public string CredentialEnvironmentVariable { get; set; } = "TEAMHUB_AI_PROVIDER_API_KEY";

    public AiProviderSettings ToProviderSettings()
    {
        var endpoint = new Uri(GetSelectedEndpoint().Trim(), UriKind.Absolute);
        if (OfflineOnly && !endpoint.IsLoopback)
        {
            throw new InvalidOperationException(
                "AI offline-only mode requires a loopback endpoint such as http://127.0.0.1 or http://localhost.");
        }

        return new AiProviderSettings(
            Provider.Trim(),
            endpoint,
            GetSelectedModel().Trim(),
            Temperature,
            TimeSpan.FromSeconds(TimeoutSeconds),
            GetSelectedCredentialEnvironmentVariable().Trim(),
            GetSelectedApiMode().Trim());
    }

    internal string GetSelectedEndpoint()
    {
        var value = GetSelectedProfile()?.Endpoint;
        return string.IsNullOrWhiteSpace(value) ? Endpoint : value;
    }

    internal string GetSelectedModel()
    {
        var value = GetSelectedProfile()?.Model;
        return string.IsNullOrWhiteSpace(value) ? Model : value;
    }

    public string GetSelectedCredentialEnvironmentVariable()
    {
        var value = GetSelectedProfile()?.CredentialEnvironmentVariable;
        return string.IsNullOrWhiteSpace(value) ? CredentialEnvironmentVariable : value;
    }

    public string GetSelectedApiMode()
    {
        var value = GetSelectedProfile()?.ApiMode;
        return string.IsNullOrWhiteSpace(value) ? ApiMode : value;
    }

    private AiProviderProfileOptions? GetSelectedProfile() =>
        Providers.FirstOrDefault(profile =>
            string.Equals(profile.Key, Provider?.Trim(), StringComparison.OrdinalIgnoreCase)).Value;

    internal static bool IsLoopbackEndpoint(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var endpoint)
        && endpoint.Scheme is "http" or "https"
        && endpoint.IsLoopback;
}

public sealed class AiChatOptions
{
    public const string SectionName = "AI:Chat";

    public bool Enabled { get; set; } = true;
    public int MaximumMessages { get; set; } = 20;
    public int MaximumMessageCharacters { get; set; } = 8_000;
    public int MaximumDocuments { get; set; } = 5;
    public int MaximumDocumentCharacters { get; set; } = 120_000;
    public int MaximumKnowledgeCharacters { get; set; } = 120_000;
    public int MaximumLocalPromptCharacters { get; set; } = 18_000;
    public int MaximumInstructionCharacters { get; set; } = 40_000;
    public int MaximumOutputTokens { get; set; } = 2_000;
    public AiMeetingNotesOptions MeetingNotes { get; set; } = new();
}

public sealed class AiMeetingNotesOptions
{
    public bool Enabled { get; set; } = true;
    public string InstructionPath { get; set; } = Path.Combine("AI", "MeetingNotes.md");
    public Dictionary<string, AiMeetingNotesStudioInstructionOptions> StudioInstructions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Legacy server-file paths remain readable so existing installations migrate without downtime.
    public Dictionary<string, string> StudioInstructionPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AiMeetingNotesStudioInstructionOptions
{
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

public sealed class AiFlowDesignerOptions
{
    public const string SectionName = "AI:FlowDesigner";

    public bool Enabled { get; set; }
    public int MaximumSourceDocuments { get; set; } = 10;
    public int MaximumSourceCharacters { get; set; } = 120_000;
    public int MaximumPromptCharacters { get; set; } = 4_000;
    public int MaximumOutputTokens { get; set; } = 8_000;
    public int MaximumDiagrams { get; set; } = 12;
    public int MaximumHierarchyDepth { get; set; } = 4;
}

public sealed class AiReportGeneratorOptions
{
    public const string SectionName = "AI:ReportGenerator";

    public bool Enabled { get; set; }
    public int MaximumConcurrentRequests { get; set; } = 1;
    public int MaximumSourceCharactersPerStudio { get; set; } = 24000;
    public int MaximumOutputTokens { get; set; } = 4000;
    public string WeeklyWorkSummaryPromptPath { get; set; } =
        Path.Combine("Prompts", "WeeklyWorkSummary.md");
}

public sealed class AiConfigurationSnapshot
{
    public AiRuntimeOptions Runtime { get; set; } = new();
    public AiChatOptions Chat { get; set; } = new();
    public AiFlowDesignerOptions FlowDesigner { get; set; } = new();
    public AiReportGeneratorOptions ReportGenerator { get; set; } = new();
    public bool HasCredential { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed record SaveAiConfigurationRequest(
    AiRuntimeOptions Runtime,
    AiChatOptions Chat,
    AiFlowDesignerOptions FlowDesigner,
    AiReportGeneratorOptions ReportGenerator,
    string? Credential,
    bool RemoveCredential,
    string UpdatedBy);

public static class AiModelProviderKeys
{
    public const string Ollama = "Ollama";
    public const string LlamaCpp = "LlamaCpp";
    public const string OpenAiCompatible = "OpenAICompatible";
    public const string PfbAiGateway = "PfbAiGateway";
}

public sealed class AiProviderProfileOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? CredentialEnvironmentVariable { get; set; }
    public string? ApiMode { get; set; }
}

public sealed record AiProviderSettings(
    string Provider,
    Uri Endpoint,
    string Model,
    double Temperature,
    TimeSpan Timeout,
    string CredentialEnvironmentVariable,
    string ApiMode = "");
