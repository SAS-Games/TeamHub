using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.AI.Contracts;
using TeamHub.Studio;

namespace TeamHub.Web.Pages.Configuration;

[Authorize(Roles = "Admin")]
public sealed class AIModel(
    IAiConfigurationService configuration,
    IAiModelService modelService,
    IAiModelProviderRegistry providers,
    IMeetingNotesTeamsConfigurationService? meetingNotesTeams = null,
    IStudioDirectoryService? studioDirectory = null) : PageModel
{
    [BindProperty]
    public AiSettingsInput Settings { get; set; } = new();

    [BindProperty, DataType(DataType.Password), StringLength(4096)]
    public string? Credential { get; set; }

    [BindProperty]
    public bool RemoveCredential { get; set; }

    [BindProperty]
    public List<MeetingNotesTeamsChannelInput> MeetingNotesTeamsChannels { get; set; } = [];

    public bool HasCredential { get; private set; }
    public DateTime? UpdatedAtUtc { get; private set; }
    public string? UpdatedBy { get; private set; }
    public AiProviderHealthResult? TestResult { get; private set; }
    public IReadOnlyList<AiModelCatalogItem> PfbModels { get; private set; } = [];
    public string? PfbModelCatalogMessage { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        await LoadAsync(cancellationToken);

    public Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken) =>
        SaveAsync(testConnection: false, cancellationToken);

    public Task<IActionResult> OnPostTestAsync(CancellationToken cancellationToken) =>
        SaveAsync(testConnection: true, cancellationToken);

    public Task<IActionResult> OnPostSaveRuntimeAsync(CancellationToken cancellationToken) =>
        SaveSectionAsync(AiConfigurationSection.Runtime, cancellationToken);

    public Task<IActionResult> OnPostSaveModelsAsync(CancellationToken cancellationToken) =>
        SaveSectionAsync(AiConfigurationSection.Models, cancellationToken);

    public Task<IActionResult> OnPostSaveSecurityAsync(CancellationToken cancellationToken) =>
        SaveSectionAsync(AiConfigurationSection.Security, cancellationToken);

    public Task<IActionResult> OnPostSaveChatAsync(CancellationToken cancellationToken) =>
        SaveSectionAsync(AiConfigurationSection.Chat, cancellationToken);

    public Task<IActionResult> OnPostSaveMeetingNotesAsync(CancellationToken cancellationToken) =>
        SaveSectionAsync(AiConfigurationSection.MeetingNotes, cancellationToken);

    public Task<IActionResult> OnPostSaveFlowDesignerAsync(CancellationToken cancellationToken) =>
        SaveSectionAsync(AiConfigurationSection.FlowDesigner, cancellationToken);

    public Task<IActionResult> OnPostSaveReportGeneratorAsync(CancellationToken cancellationToken) =>
        SaveSectionAsync(AiConfigurationSection.ReportGenerator, cancellationToken);

    public async Task<IActionResult> OnPostTestConnectionAsync(CancellationToken cancellationToken)
    {
        ModelState.Clear();
        TestResult = await modelService.TestConnectionAsync(cancellationToken);
        StatusMessage = TestResult.IsAvailable && TestResult.IsModelAvailable
            ? $"Connection succeeded. {TestResult.Provider} model '{TestResult.Model}' is available."
            : $"Connection check failed. {TestResult.Message}";
        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task<IActionResult> SaveSectionAsync(
        AiConfigurationSection section,
        CancellationToken cancellationToken)
    {
        ModelState.Clear();
        try
        {
            var snapshot = await configuration.GetAsync(cancellationToken);
            switch (section)
            {
                case AiConfigurationSection.Runtime:
                    snapshot.Runtime.Provider = Settings.Provider?.Trim() ?? string.Empty;
                    snapshot.Runtime.OfflineOnly = Settings.OfflineOnly;
                    snapshot.Runtime.Temperature = Settings.Temperature;
                    snapshot.Runtime.TimeoutSeconds = Settings.TimeoutSeconds;
                    break;
                case AiConfigurationSection.Models:
                    snapshot.Runtime.Providers = Settings.ToRuntimeOptions().Providers;
                    break;
                case AiConfigurationSection.Security:
                    snapshot.Runtime.CredentialEnvironmentVariable =
                        Settings.CredentialEnvironmentVariable?.Trim() ?? string.Empty;
                    break;
                case AiConfigurationSection.Chat:
                    var meetingNotes = snapshot.Chat.MeetingNotes;
                    snapshot.Chat = Settings.ToChatOptions();
                    snapshot.Chat.MeetingNotes = meetingNotes;
                    break;
                case AiConfigurationSection.MeetingNotes:
                    snapshot.Chat.MeetingNotes = new AiMeetingNotesOptions
                    {
                        Enabled = Settings.MeetingNotesEnabled,
                        InstructionPath = Settings.MeetingNotesInstructionPath?.Trim() ?? string.Empty
                    };
                    break;
                case AiConfigurationSection.FlowDesigner:
                    snapshot.FlowDesigner = Settings.ToFlowDesignerOptions();
                    break;
                case AiConfigurationSection.ReportGenerator:
                    snapshot.ReportGenerator = Settings.ToReportGeneratorOptions();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(section));
            }

            var savesCredential = section == AiConfigurationSection.Security
                && snapshot.Runtime.Provider != AiModelProviderKeys.PfbAiGateway;
            await configuration.SaveAsync(new SaveAiConfigurationRequest(
                snapshot.Runtime,
                snapshot.Chat,
                snapshot.FlowDesigner,
                snapshot.ReportGenerator,
                savesCredential ? Credential : null,
                savesCredential && RemoveCredential,
                User.Identity?.Name ?? "Administrator"), cancellationToken);

            if (section == AiConfigurationSection.MeetingNotes && meetingNotesTeams is not null)
            {
                await meetingNotesTeams.SaveAsync(
                    MeetingNotesTeamsChannels.Select(row => new SaveMeetingNotesTeamsChannel(
                        row.StudioId,
                        row.WebhookUrl,
                        row.Remove)).ToList(),
                    User.Identity?.Name ?? "Administrator",
                    cancellationToken);
            }

            StatusMessage = section switch
            {
                AiConfigurationSection.Runtime => "Runtime settings saved.",
                AiConfigurationSection.Models => "Provider profiles saved.",
                AiConfigurationSection.Security => "Provider security settings saved.",
                AiConfigurationSection.Chat => "TeamHub Assistant settings saved.",
                AiConfigurationSection.MeetingNotes => "Meeting Notes and Teams destinations saved.",
                AiConfigurationSection.FlowDesigner => "Flow Designer AI settings saved.",
                AiConfigurationSection.ReportGenerator => "Report Generator settings saved.",
                _ => "AI settings saved."
            };
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or AiModelProviderException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadMetadataAsync(cancellationToken);
            await LoadMeetingNotesChannelsAsync(preserveInput: true, cancellationToken);
            await LoadPfbModelsAsync(cancellationToken);
            return Page();
        }
    }

    private async Task<IActionResult> SaveAsync(bool testConnection, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadMetadataAsync(cancellationToken);
            await LoadMeetingNotesChannelsAsync(preserveInput: true, cancellationToken);
            return Page();
        }

        try
        {
            await configuration.SaveAsync(new SaveAiConfigurationRequest(
                Settings.ToRuntimeOptions(),
                Settings.ToChatOptions(),
                Settings.ToFlowDesignerOptions(),
                Settings.ToReportGeneratorOptions(),
                Settings.Provider == AiModelProviderKeys.PfbAiGateway ? null : Credential,
                Settings.Provider != AiModelProviderKeys.PfbAiGateway && RemoveCredential,
                User.Identity?.Name ?? "Administrator"), cancellationToken);

            if (meetingNotesTeams is not null)
            {
                await meetingNotesTeams.SaveAsync(
                    MeetingNotesTeamsChannels.Select(row => new SaveMeetingNotesTeamsChannel(
                        row.StudioId,
                        row.WebhookUrl,
                        row.Remove)).ToList(),
                    User.Identity?.Name ?? "Administrator",
                    cancellationToken);
            }

            if (!testConnection)
            {
                StatusMessage = "AI configuration saved. New requests will use it immediately.";
                return RedirectToPage();
            }

            TestResult = await modelService.TestConnectionAsync(cancellationToken);
            StatusMessage = TestResult.IsAvailable && TestResult.IsModelAvailable
                ? $"Connection succeeded. {TestResult.Provider} model '{TestResult.Model}' is available."
                : $"Connection check failed. {TestResult.Message}";
            await LoadAsync(cancellationToken);
            return Page();
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or AiModelProviderException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadMetadataAsync(cancellationToken);
            await LoadMeetingNotesChannelsAsync(preserveInput: true, cancellationToken);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await configuration.GetAsync(cancellationToken);
        Settings = AiSettingsInput.FromSnapshot(snapshot);
        HasCredential = snapshot.HasCredential;
        UpdatedAtUtc = snapshot.UpdatedAtUtc;
        UpdatedBy = snapshot.UpdatedBy;
        await LoadMeetingNotesChannelsAsync(preserveInput: false, cancellationToken);
        await LoadPfbModelsAsync(cancellationToken);
    }

    private async Task LoadMeetingNotesChannelsAsync(
        bool preserveInput,
        CancellationToken cancellationToken)
    {
        if (meetingNotesTeams is null || studioDirectory is null) return;
        var statuses = (await meetingNotesTeams.ListAsync(cancellationToken))
            .ToDictionary(item => item.StudioId, StringComparer.OrdinalIgnoreCase);
        var submitted = preserveInput
            ? MeetingNotesTeamsChannels.ToDictionary(item => item.StudioId, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, MeetingNotesTeamsChannelInput>(StringComparer.OrdinalIgnoreCase);
        MeetingNotesTeamsChannels = (await studioDirectory.GetStudiosAsync(cancellationToken))
            .Where(studio => studio.IsActive)
            .OrderBy(studio => studio.StudioName, StringComparer.OrdinalIgnoreCase)
            .Select(studio =>
            {
                statuses.TryGetValue(studio.Id, out var status);
                submitted.TryGetValue(studio.Id, out var input);
                return new MeetingNotesTeamsChannelInput
                {
                    StudioId = studio.Id,
                    StudioName = studio.StudioName,
                    WebhookUrl = input?.WebhookUrl,
                    IsConfigured = status?.IsConfigured == true,
                    Remove = input?.Remove == true
                };
            })
            .ToList();
    }

    private async Task LoadMetadataAsync(CancellationToken cancellationToken)
    {
        var snapshot = await configuration.GetAsync(cancellationToken);
        HasCredential = snapshot.HasCredential;
        UpdatedAtUtc = snapshot.UpdatedAtUtc;
        UpdatedBy = snapshot.UpdatedBy;
    }

    private async Task LoadPfbModelsAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(Settings.Provider, AiModelProviderKeys.PfbAiGateway, StringComparison.Ordinal))
        {
            PfbModelCatalogMessage = "Select Sony PFB AI Gateway and save to load its model catalog.";
            return;
        }

        try
        {
            var endpoint = new Uri(Settings.PfbAiGatewayEndpoint.Trim(), UriKind.Absolute);
            var provider = providers.GetRequired(AiModelProviderKeys.PfbAiGateway);
            if (provider is not IAiModelCatalogProvider catalog)
            {
                PfbModelCatalogMessage = "This provider does not expose a model catalog.";
                return;
            }

            PfbModels = await catalog.GetModelsAsync(new AiProviderSettings(
                AiModelProviderKeys.PfbAiGateway,
                endpoint,
                Settings.PfbAiGatewayModel,
                Settings.Temperature,
                TimeSpan.FromSeconds(Settings.TimeoutSeconds),
                Settings.CredentialEnvironmentVariable?.Trim() ?? string.Empty,
                Settings.PfbAiGatewayApiMode), cancellationToken);
            PfbModelCatalogMessage = PfbModels.Count == 0
                ? "The gateway returned an empty model catalog."
                : $"Loaded {PfbModels.Count} models from the PFB AI Gateway.";
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or AiModelProviderException)
        {
            PfbModelCatalogMessage = $"Could not load the PFB model catalog: {exception.Message}";
        }
    }
}

public sealed class MeetingNotesTeamsChannelInput
{
    [Required, StringLength(100)]
    public string StudioId { get; set; } = string.Empty;
    public string StudioName { get; set; } = string.Empty;
    [StringLength(4096), DataType(DataType.Password)]
    public string? WebhookUrl { get; set; }
    public bool IsConfigured { get; set; }
    public bool Remove { get; set; }
}

internal enum AiConfigurationSection
{
    Runtime,
    Models,
    Security,
    Chat,
    MeetingNotes,
    FlowDesigner,
    ReportGenerator
}

public sealed class AiSettingsInput
{
    [Required]
    public string Provider { get; set; } = AiModelProviderKeys.Ollama;

    [Required, StringLength(2048)]
    public string OllamaEndpoint { get; set; } = "http://127.0.0.1:11434";
    [Required, StringLength(256)]
    public string OllamaModel { get; set; } = "qwen3:8b";

    [Required, StringLength(2048)]
    public string LlamaCppEndpoint { get; set; } = "http://127.0.0.1:8080";
    [Required, StringLength(256)]
    public string LlamaCppModel { get; set; } = "qwen3:8b";

    [Required, StringLength(2048)]
    public string OpenAiCompatibleEndpoint { get; set; } = "http://127.0.0.1:1234";
    [Required, StringLength(256)]
    public string OpenAiCompatibleModel { get; set; } = "local-model";

    [Required, StringLength(2048)]
    public string PfbAiGatewayEndpoint { get; set; } = "https://ai-gateway.dspprod.bis.sie.sony.com";
    [Required, StringLength(256)]
    public string PfbAiGatewayModel { get; set; } = "gpt-5-nano";
    [Required]
    public string PfbAiGatewayApiMode { get; set; } = "CommonChat";

    public bool OfflineOnly { get; set; } = true;
    [Display(Name = "Enable TeamHub Assistant")]
    public bool ChatEnabled { get; set; }
    [Display(Name = "Maximum messages per conversation")]
    [Range(1, 100)] public int ChatMaximumMessages { get; set; } = 20;
    [Display(Name = "Maximum characters per message")]
    [Range(100, 100_000)] public int ChatMaximumMessageCharacters { get; set; } = 8_000;
    [Display(Name = "Maximum attached documents")]
    [Range(0, 20)] public int ChatMaximumDocuments { get; set; } = 5;
    [Display(Name = "Maximum combined document characters")]
    [Range(1_000, 1_000_000)] public int ChatMaximumDocumentCharacters { get; set; } = 120_000;
    [Display(Name = "Maximum TeamHub knowledge characters")]
    [Range(1_000, 1_000_000)] public int ChatMaximumKnowledgeCharacters { get; set; } = 120_000;
    [Display(Name = "Maximum local-model prompt characters")]
    [Range(2_000, 200_000)] public int ChatMaximumLocalPromptCharacters { get; set; } = 18_000;
    [Display(Name = "Maximum TeamHub instruction characters")]
    [Range(1_000, 200_000)] public int ChatMaximumInstructionCharacters { get; set; } = 40_000;
    [Display(Name = "Maximum output tokens per AI request")]
    [Range(128, 32_000)] public int ChatMaximumOutputTokens { get; set; } = 2_000;
    [Display(Name = "Enable Meeting Notes mode")]
    public bool MeetingNotesEnabled { get; set; } = true;
    [Required, StringLength(2048), Display(Name = "Meeting Notes instruction file")]
    public string MeetingNotesInstructionPath { get; set; } = Path.Combine("AI", "MeetingNotes.md");
    [Range(0, 2)] public double Temperature { get; set; }
    [Range(1, 1800)] public int TimeoutSeconds { get; set; } = 180;
    [StringLength(256)] public string CredentialEnvironmentVariable { get; set; } = "TEAMHUB_AI_PROVIDER_API_KEY";

    public bool FlowDesignerEnabled { get; set; }
    [Display(Name = "Maximum source documents per request")]
    [Range(1, 50)] public int MaximumSourceDocuments { get; set; } = 10;
    [Display(Name = "Maximum combined source characters")]
    [Range(1_000, 2_000_000)] public int MaximumSourceCharacters { get; set; } = 120_000;
    [Display(Name = "Maximum instruction characters")]
    [Range(100, 20_000)] public int MaximumPromptCharacters { get; set; } = 4_000;
    [Display(Name = "Maximum output tokens per AI request")]
    [Range(1_000, 32_000)] public int MaximumOutputTokens { get; set; } = 8_000;
    [Range(1, 25)] public int MaximumDiagrams { get; set; } = 12;
    [Range(1, 6)] public int MaximumHierarchyDepth { get; set; } = 4;
    [Display(Name = "Maximum concurrent reports")]
    [Range(1, 16)] public int ReportMaximumConcurrentRequests { get; set; } = 1;
    public bool ReportGeneratorEnabled { get; set; }
    [Display(Name = "Maximum source characters per studio")]
    [Range(1, 120000)] public int ReportMaximumSourceCharactersPerStudio { get; set; } = 24000;
    [Display(Name = "Maximum output tokens per studio")]
    [Range(1, 16000)] public int ReportMaximumOutputTokens { get; set; } = 4000;
    [Required, StringLength(2048)]
    public string WeeklyWorkSummaryPromptPath { get; set; } = Path.Combine("Prompts", "WeeklyWorkSummary.md");

    public AiReportGeneratorOptions ToReportGeneratorOptions() => new()
    {
        Enabled = ReportGeneratorEnabled,
        MaximumConcurrentRequests = ReportMaximumConcurrentRequests,
        MaximumSourceCharactersPerStudio = ReportMaximumSourceCharactersPerStudio,
        MaximumOutputTokens = ReportMaximumOutputTokens,
        WeeklyWorkSummaryPromptPath = WeeklyWorkSummaryPromptPath?.Trim() ?? string.Empty
    };

    public AiChatOptions ToChatOptions() => new()
    {
        Enabled = ChatEnabled,
        MaximumMessages = ChatMaximumMessages,
        MaximumMessageCharacters = ChatMaximumMessageCharacters,
        MaximumDocuments = ChatMaximumDocuments,
        MaximumDocumentCharacters = ChatMaximumDocumentCharacters,
        MaximumKnowledgeCharacters = ChatMaximumKnowledgeCharacters,
        MaximumLocalPromptCharacters = ChatMaximumLocalPromptCharacters,
        MaximumInstructionCharacters = ChatMaximumInstructionCharacters,
        MaximumOutputTokens = ChatMaximumOutputTokens,
        MeetingNotes = new AiMeetingNotesOptions
        {
            Enabled = MeetingNotesEnabled,
            InstructionPath = MeetingNotesInstructionPath?.Trim() ?? string.Empty
        }
    };

    public AiRuntimeOptions ToRuntimeOptions()
    {
        var profiles = new Dictionary<string, AiProviderProfileOptions>(StringComparer.OrdinalIgnoreCase)
        {
            [AiModelProviderKeys.Ollama] = Profile(OllamaEndpoint, OllamaModel),
            [AiModelProviderKeys.LlamaCpp] = Profile(LlamaCppEndpoint, LlamaCppModel),
            [AiModelProviderKeys.OpenAiCompatible] = Profile(OpenAiCompatibleEndpoint, OpenAiCompatibleModel),
            [AiModelProviderKeys.PfbAiGateway] = Profile(
                PfbAiGatewayEndpoint,
                PfbAiGatewayModel,
                PfbAiGatewayApiMode)
        };
        var selected = profiles.TryGetValue(Provider, out var profile)
            ? profile
            : profiles[AiModelProviderKeys.Ollama];
        return new AiRuntimeOptions
        {
            Provider = Provider,
            Endpoint = selected.Endpoint,
            Model = selected.Model,
            Providers = profiles,
            OfflineOnly = OfflineOnly,
            Temperature = Temperature,
            TimeoutSeconds = TimeoutSeconds,
            CredentialEnvironmentVariable = CredentialEnvironmentVariable?.Trim() ?? string.Empty
        };
    }

    public AiFlowDesignerOptions ToFlowDesignerOptions() => new()
    {
        Enabled = FlowDesignerEnabled,
        MaximumSourceDocuments = MaximumSourceDocuments,
        MaximumSourceCharacters = MaximumSourceCharacters,
        MaximumPromptCharacters = MaximumPromptCharacters,
        MaximumOutputTokens = MaximumOutputTokens,
        MaximumDiagrams = MaximumDiagrams,
        MaximumHierarchyDepth = MaximumHierarchyDepth
    };

    public static AiSettingsInput FromSnapshot(AiConfigurationSnapshot snapshot)
    {
        var runtime = snapshot.Runtime;
        var ollama = GetProfile(runtime, AiModelProviderKeys.Ollama, "http://127.0.0.1:11434", "qwen3:8b");
        var llamaCpp = GetProfile(runtime, AiModelProviderKeys.LlamaCpp, "http://127.0.0.1:8080", "qwen3:8b");
        var compatible = GetProfile(runtime, AiModelProviderKeys.OpenAiCompatible, runtime.Endpoint, runtime.Model);
        var pfb = GetProfile(
            runtime,
            AiModelProviderKeys.PfbAiGateway,
            "https://ai-gateway.dspprod.bis.sie.sony.com",
            "gpt-5-nano",
            "CommonChat");
        return new AiSettingsInput
        {
            Provider = runtime.Provider,
            OllamaEndpoint = ollama.Endpoint,
            OllamaModel = ollama.Model,
            LlamaCppEndpoint = llamaCpp.Endpoint,
            LlamaCppModel = llamaCpp.Model,
            OpenAiCompatibleEndpoint = compatible.Endpoint,
            OpenAiCompatibleModel = compatible.Model,
            PfbAiGatewayEndpoint = pfb.Endpoint,
            PfbAiGatewayModel = pfb.Model,
            PfbAiGatewayApiMode = string.IsNullOrWhiteSpace(pfb.ApiMode) ? "CommonChat" : pfb.ApiMode,
            ChatEnabled = snapshot.Chat.Enabled,
            ChatMaximumMessages = snapshot.Chat.MaximumMessages,
            ChatMaximumMessageCharacters = snapshot.Chat.MaximumMessageCharacters,
            ChatMaximumDocuments = snapshot.Chat.MaximumDocuments,
            ChatMaximumDocumentCharacters = snapshot.Chat.MaximumDocumentCharacters,
            ChatMaximumKnowledgeCharacters = snapshot.Chat.MaximumKnowledgeCharacters,
            ChatMaximumLocalPromptCharacters = snapshot.Chat.MaximumLocalPromptCharacters,
            ChatMaximumInstructionCharacters = snapshot.Chat.MaximumInstructionCharacters,
            ChatMaximumOutputTokens = snapshot.Chat.MaximumOutputTokens,
            MeetingNotesEnabled = snapshot.Chat.MeetingNotes.Enabled,
            MeetingNotesInstructionPath = snapshot.Chat.MeetingNotes.InstructionPath,
            OfflineOnly = runtime.OfflineOnly,
            Temperature = runtime.Temperature,
            TimeoutSeconds = runtime.TimeoutSeconds,
            CredentialEnvironmentVariable = runtime.CredentialEnvironmentVariable,
            FlowDesignerEnabled = snapshot.FlowDesigner.Enabled,
            MaximumSourceDocuments = snapshot.FlowDesigner.MaximumSourceDocuments,
            MaximumSourceCharacters = snapshot.FlowDesigner.MaximumSourceCharacters,
            MaximumPromptCharacters = snapshot.FlowDesigner.MaximumPromptCharacters,
            MaximumOutputTokens = snapshot.FlowDesigner.MaximumOutputTokens,
            MaximumDiagrams = snapshot.FlowDesigner.MaximumDiagrams,
            MaximumHierarchyDepth = snapshot.FlowDesigner.MaximumHierarchyDepth,
            ReportMaximumConcurrentRequests = snapshot.ReportGenerator.MaximumConcurrentRequests,
            ReportGeneratorEnabled = snapshot.ReportGenerator.Enabled,
            ReportMaximumSourceCharactersPerStudio = snapshot.ReportGenerator.MaximumSourceCharactersPerStudio,
            ReportMaximumOutputTokens = snapshot.ReportGenerator.MaximumOutputTokens,
            WeeklyWorkSummaryPromptPath = snapshot.ReportGenerator.WeeklyWorkSummaryPromptPath
        };
    }


    private static AiProviderProfileOptions Profile(
        string endpoint,
        string model,
        string? apiMode = null) => new()
    {
        Endpoint = endpoint?.Trim() ?? string.Empty,
        Model = model?.Trim() ?? string.Empty,
        ApiMode = apiMode?.Trim()
    };

    private static AiProviderProfileOptions GetProfile(
        AiRuntimeOptions runtime,
        string key,
        string fallbackEndpoint,
        string fallbackModel,
        string? fallbackApiMode = null) =>
        runtime.Providers.FirstOrDefault(item =>
            string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)).Value
        ?? Profile(fallbackEndpoint, fallbackModel, fallbackApiMode);
}
