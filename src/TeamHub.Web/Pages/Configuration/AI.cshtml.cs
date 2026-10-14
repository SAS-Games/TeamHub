using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.AI.Contracts;

namespace TeamHub.Web.Pages.Configuration;

[Authorize(Roles = "Admin")]
public sealed class AIModel(
    IAiConfigurationService configuration,
    IAiModelService modelService,
    IAiModelProviderRegistry providers) : PageModel
{
    [BindProperty]
    public AiSettingsInput Settings { get; set; } = new();

    [BindProperty, DataType(DataType.Password), StringLength(4096)]
    public string? Credential { get; set; }

    [BindProperty]
    public bool RemoveCredential { get; set; }

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

    private async Task<IActionResult> SaveAsync(bool testConnection, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadMetadataAsync(cancellationToken);
            return Page();
        }

        try
        {
            await configuration.SaveAsync(new SaveAiConfigurationRequest(
                Settings.ToRuntimeOptions(),
                Settings.ToFlowDesignerOptions(),
                new AiReportGeneratorOptions
                {
                    Enabled = false,
                    MaximumConcurrentRequests = Settings.ReportMaximumConcurrentRequests
                },
                Credential,
                RemoveCredential,
                User.Identity?.Name ?? "Administrator"), cancellationToken);

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
        await LoadPfbModelsAsync(cancellationToken);
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
    [Range(0, 2)] public double Temperature { get; set; }
    [Range(1, 1800)] public int TimeoutSeconds { get; set; } = 180;
    [StringLength(256)] public string CredentialEnvironmentVariable { get; set; } = "TEAMHUB_AI_PROVIDER_API_KEY";

    public bool FlowDesignerEnabled { get; set; }
    [Range(1, 50)] public int MaximumSourceDocuments { get; set; } = 10;
    [Range(1_000, 2_000_000)] public int MaximumSourceCharacters { get; set; } = 120_000;
    [Range(100, 20_000)] public int MaximumPromptCharacters { get; set; } = 4_000;
    [Range(1_000, 32_000)] public int MaximumOutputTokens { get; set; } = 8_000;
    [Range(1, 25)] public int MaximumDiagrams { get; set; } = 12;
    [Range(1, 6)] public int MaximumHierarchyDepth { get; set; } = 4;
    [Range(1, 16)] public int ReportMaximumConcurrentRequests { get; set; } = 1;

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
            ReportMaximumConcurrentRequests = snapshot.ReportGenerator.MaximumConcurrentRequests
        };
    }

    private static AiProviderProfileOptions Profile(
        string endpoint,
        string model,
        string? apiMode = null) => new()
    {
        Endpoint = endpoint.Trim(),
        Model = model.Trim(),
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
