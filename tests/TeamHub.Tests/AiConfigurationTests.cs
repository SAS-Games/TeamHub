using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.AI;
using TeamHub.AI.Contracts;
using TeamHub.Domain.Entities;
using TeamHub.Infrastructure.Persistence;
using TeamHub.Infrastructure.Services;
using TeamHub.Web.Pages.Configuration;
using TeamHub.Studio;
using TeamHub.Web.AI;
using System.Security.Claims;

namespace TeamHub.Tests;

public sealed class AiConfigurationTests
{
    [Fact]
    public async Task GatewayUsesOnlyTheAuthenticatedUsersPersonalToken()
    {
        await using var context = CreateDbContext();
        using var defaults = CreateOptions();
        var configuration = new AiConfigurationService(context, new EphemeralDataProtectionProvider(),
            defaults.GetRequiredService<IOptionsMonitor<AiRuntimeOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiChatOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiFlowDesignerOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiReportGeneratorOptions>>());
        await configuration.SaveAsync(new(new(), new(), new(), new(), "shared-token", false, "admin"));
        var personal = new PersonalGatewayCredentials();
        await personal.SaveTokenAsync("one@example.com", "one-token");
        await personal.SaveTokenAsync("two@example.com", "two-token");
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var accessor = new PersonalAiProviderCredentialAccessor(http, personal, configuration);
        var settings = new AiRuntimeOptions
        {
            Provider = AiModelProviderKeys.PfbAiGateway, OfflineOnly = false,
            Endpoint = "https://gateway.example.test", Model = "gateway-model"
        }.ToProviderSettings();
        http.HttpContext.User = SignedIn("one@example.com");
        (await accessor.GetCredentialAsync(settings)).Should().Be("one-token");
        http.HttpContext.User = SignedIn("two@example.com");
        (await accessor.GetCredentialAsync(settings)).Should().Be("two-token");
        http.HttpContext.User = SignedIn("missing@example.com");
        var missing = async () => await accessor.GetCredentialAsync(settings);
        await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*My Connections*");
        http.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "one@example.com")]));
        await missing.Should().ThrowAsync<InvalidOperationException>();
        http.HttpContext = null;
        await missing.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(AiModelProviderKeys.Ollama)]
    [InlineData(AiModelProviderKeys.LlamaCpp)]
    [InlineData(AiModelProviderKeys.OpenAiCompatible)]
    public async Task OtherProvidersRetainSharedCredentialResolution(string providerKey)
    {
        await using var context = CreateDbContext();
        using var defaults = CreateOptions();
        var configuration = new AiConfigurationService(context, new EphemeralDataProtectionProvider(),
            defaults.GetRequiredService<IOptionsMonitor<AiRuntimeOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiChatOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiFlowDesignerOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiReportGeneratorOptions>>());
        await configuration.SaveAsync(new(new(), new(), new(), new(), "shared-token", false, "admin"));
        var accessor = new PersonalAiProviderCredentialAccessor(new HttpContextAccessor(), new PersonalGatewayCredentials(), configuration);
        var settings = new AiRuntimeOptions { Provider = providerKey }.ToProviderSettings();
        (await accessor.GetCredentialAsync(settings)).Should().Be("shared-token");
    }

    private static ClaimsPrincipal SignedIn(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, userId)], "Test"));

    private sealed class PersonalGatewayCredentials : IPfbAiGatewayUserCredentialService
    {
        private readonly Dictionary<string, string?> tokens = new(StringComparer.OrdinalIgnoreCase);
        public Task<bool> HasTokenAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(tokens.TryGetValue(userId, out var token) && !string.IsNullOrWhiteSpace(token));
        public Task<string?> GetTokenAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(tokens.GetValueOrDefault(userId));
        public Task SaveTokenAsync(string userId, string? token, bool removeToken = false, CancellationToken cancellationToken = default)
        {
            tokens[userId] = removeToken ? null : token;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Saved_settings_override_bootstrap_defaults_and_encrypt_the_credential()
    {
        await using var context = CreateDbContext();
        using var options = CreateOptions();
        var service = new AiConfigurationService(
            context,
            new EphemeralDataProtectionProvider(),
            options.GetRequiredService<IOptionsMonitor<AiRuntimeOptions>>(),
            options.GetRequiredService<IOptionsMonitor<AiChatOptions>>(),
            options.GetRequiredService<IOptionsMonitor<AiFlowDesignerOptions>>(),
            options.GetRequiredService<IOptionsMonitor<AiReportGeneratorOptions>>());

        await service.SaveAsync(new SaveAiConfigurationRequest(
            new AiRuntimeOptions
            {
                Provider = AiModelProviderKeys.LlamaCpp,
                Providers = new Dictionary<string, AiProviderProfileOptions>
                {
                    [AiModelProviderKeys.LlamaCpp] = new()
                    {
                        Endpoint = "http://127.0.0.1:8080",
                        Model = "configured-model"
                    }
                },
                OfflineOnly = true,
                Temperature = 0.2,
                TimeoutSeconds = 90,
                CredentialEnvironmentVariable = "AI_TEST_KEY"
            },
            new AiChatOptions { Enabled = true, MaximumOutputTokens = 6_000 },
            new AiFlowDesignerOptions { Enabled = true },
            new AiReportGeneratorOptions { Enabled = false },
            "super-secret",
            false,
            "admin@example.com"));

        context.ChangeTracker.Clear();
        var stored = await context.AiConfiguration.SingleAsync();
        stored.SettingsJson.Should().Contain("configured-model");
        stored.SettingsJson.Should().NotContain("super-secret");
        stored.CredentialProtected.Should().NotBeNullOrWhiteSpace().And.NotContain("super-secret");

        var current = await service.GetAsync();
        current.Runtime.Provider.Should().Be(AiModelProviderKeys.LlamaCpp);
        current.Chat.Enabled.Should().BeTrue();
        current.Chat.MaximumOutputTokens.Should().Be(6_000);
        current.FlowDesigner.Enabled.Should().BeTrue();
        current.HasCredential.Should().BeTrue();
        current.UpdatedBy.Should().Be("admin@example.com");
        var credential = await service.GetCredentialAsync(current.Runtime.ToProviderSettings());
        credential.Should().Be("super-secret");
    }

    [Fact]
    public async Task Database_initializer_adds_ai_configuration_table_to_an_existing_database()
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        await using var context = new WorkflowDbContext(options);
        await context.Database.OpenConnectionAsync();

        await new WorkflowDatabaseInitializer(context).InitializeAsync();

        var tables = await context.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name = 'AiConfiguration'")
            .ToListAsync();
        tables.Should().ContainSingle().Which.Should().Be("AiConfiguration");
    }

    [Fact]
    public async Task Legacy_saved_chat_enabled_setting_migrates_to_the_AI_Chat_section()
    {
        await using var context = CreateDbContext();
        context.AiConfiguration.Add(new AiConfigurationRecord
        {
            Id = 1,
            SettingsJson = """{"runtime":{"chatEnabled":true},"flowDesigner":{},"reportGenerator":{}}""",
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedBy = "legacy-admin"
        });
        await context.SaveChangesAsync();
        using var defaults = CreateOptions();
        var configuration = new AiConfigurationService(
            context, new EphemeralDataProtectionProvider(),
            defaults.GetRequiredService<IOptionsMonitor<AiRuntimeOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiChatOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiFlowDesignerOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiReportGeneratorOptions>>());

        var snapshot = await configuration.GetAsync();

        snapshot.Chat.Enabled.Should().BeTrue();
        snapshot.Chat.MaximumMessages.Should().Be(20);
        snapshot.Chat.MaximumOutputTokens.Should().Be(2_000);
    }

    [Fact]
    public async Task Ai_page_saves_and_reloads_report_settings_and_uses_shared_provider_and_credential()
    {
        await using var context = CreateDbContext();
        using var defaults = CreateOptions();
        var configuration = new AiConfigurationService(
            context, new EphemeralDataProtectionProvider(),
            defaults.GetRequiredService<IOptionsMonitor<AiRuntimeOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiChatOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiFlowDesignerOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiReportGeneratorOptions>>());
        var probe = new RecordingProvider(configuration);
        var services = new ServiceCollection();
        services.AddTeamHubAi(new ConfigurationBuilder().Build());
        services.RemoveAll<IAiModelProvider>();
        services.AddSingleton<IAiModelProvider>(probe);
        services.AddSingleton<IAiConfigurationService>(configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var models = scope.ServiceProvider.GetRequiredService<IAiModelService>();
        var page = new AIModel(configuration, models, scope.ServiceProvider.GetRequiredService<IAiModelProviderRegistry>())
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
            Settings = new AiSettingsInput
            {
                Provider = AiModelProviderKeys.LlamaCpp,
                ChatEnabled = true,
                ChatMaximumMessages = 30,
                ChatMaximumMessageCharacters = 9000,
                ChatMaximumDocuments = 7,
                ChatMaximumDocumentCharacters = 130000,
                ChatMaximumKnowledgeCharacters = 140000,
                ChatMaximumLocalPromptCharacters = 20000,
                ChatMaximumInstructionCharacters = 50000,
                ChatMaximumOutputTokens = 6000,
                LlamaCppModel = "saved-model",
                FlowDesignerEnabled = true,
                ReportGeneratorEnabled = true,
                ReportMaximumConcurrentRequests = 3,
                ReportMaximumSourceCharactersPerStudio = 5000,
                ReportMaximumOutputTokens = 2500,
                WeeklyWorkSummaryPromptPath = "Prompts/custom.md"
            },
            Credential = "synthetic-provider-key"
        };
        (await page.OnPostSaveAsync(CancellationToken.None)).Should().BeOfType<RedirectToPageResult>();
        context.ChangeTracker.Clear();
        await page.OnGetAsync(CancellationToken.None);
        page.Settings.ReportGeneratorEnabled.Should().BeTrue();
        page.Settings.ReportMaximumConcurrentRequests.Should().Be(3);
        page.Settings.ReportMaximumSourceCharactersPerStudio.Should().Be(5000);
        page.Settings.ReportMaximumOutputTokens.Should().Be(2500);
        page.Settings.WeeklyWorkSummaryPromptPath.Should().Be("Prompts/custom.md");
        page.Settings.FlowDesignerEnabled.Should().BeTrue();
        page.Settings.ChatEnabled.Should().BeTrue();
        page.Settings.ChatMaximumMessages.Should().Be(30);
        page.Settings.ChatMaximumMessageCharacters.Should().Be(9000);
        page.Settings.ChatMaximumDocuments.Should().Be(7);
        page.Settings.ChatMaximumDocumentCharacters.Should().Be(130000);
        page.Settings.ChatMaximumKnowledgeCharacters.Should().Be(140000);
        page.Settings.ChatMaximumLocalPromptCharacters.Should().Be(20000);
        page.Settings.ChatMaximumInstructionCharacters.Should().Be(50000);
        page.Settings.ChatMaximumOutputTokens.Should().Be(6000);
        page.Credential = null;
        (await page.OnPostTestAsync(CancellationToken.None)).Should().BeOfType<PageResult>();
        page.TestResult!.IsAvailable.Should().BeTrue();
        probe.Settings!.Model.Should().Be("saved-model");
        probe.Credential.Should().Be("synthetic-provider-key");
        (await configuration.GetAsync()).ReportGenerator.Enabled.Should().BeTrue();
        (await configuration.GetAsync()).Chat.Enabled.Should().BeTrue();
        (await configuration.GetAsync()).Chat.MaximumOutputTokens.Should().Be(6000);

        page.Settings.ReportGeneratorEnabled = false;
        await page.OnPostSaveAsync(CancellationToken.None);
        (await configuration.GetAsync()).ReportGenerator.Enabled.Should().BeFalse();
        (await configuration.GetAsync()).FlowDesigner.Enabled.Should().BeTrue();

        page.Settings.Provider = AiModelProviderKeys.PfbAiGateway;
        page.Settings.OfflineOnly = false;
        page.Credential = "must-not-replace-shared-token";
        page.RemoveCredential = true;
        (await page.OnPostSaveAsync(CancellationToken.None)).Should().BeOfType<RedirectToPageResult>();
        (await configuration.GetCredentialAsync(new AiRuntimeOptions().ToProviderSettings()))
            .Should().Be("synthetic-provider-key");
    }

    [Theory]
    [InlineData(0, 24000, 4000, "Prompts/WeeklyWorkSummary.md")]
    [InlineData(17, 24000, 4000, "Prompts/WeeklyWorkSummary.md")]
    [InlineData(1, 0, 4000, "Prompts/WeeklyWorkSummary.md")]
    [InlineData(1, 120001, 4000, "Prompts/WeeklyWorkSummary.md")]
    [InlineData(1, 24000, 0, "Prompts/WeeklyWorkSummary.md")]
    [InlineData(1, 24000, 16001, "Prompts/WeeklyWorkSummary.md")]
    [InlineData(1, 24000, 4000, " ")]
    public async Task Saved_report_settings_reject_invalid_limits(int concurrency, int source, int output, string prompt)
    {
        await using var context = CreateDbContext();
        using var defaults = CreateOptions();
        var configuration = new AiConfigurationService(
            context, new EphemeralDataProtectionProvider(),
            defaults.GetRequiredService<IOptionsMonitor<AiRuntimeOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiChatOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiFlowDesignerOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiReportGeneratorOptions>>());
        var save = () => configuration.SaveAsync(new SaveAiConfigurationRequest(
            new AiRuntimeOptions(), new AiChatOptions(), new AiFlowDesignerOptions(),
            new AiReportGeneratorOptions
            {
                Enabled = true, MaximumConcurrentRequests = concurrency,
                MaximumSourceCharactersPerStudio = source, MaximumOutputTokens = output,
                WeeklyWorkSummaryPromptPath = prompt
            }, null, false, "admin@example.com"));
        await save.Should().ThrowAsync<ArgumentException>();
        (await context.AiConfiguration.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Saved_chat_settings_reject_invalid_limits()
    {
        await using var context = CreateDbContext();
        using var defaults = CreateOptions();
        var configuration = new AiConfigurationService(
            context, new EphemeralDataProtectionProvider(),
            defaults.GetRequiredService<IOptionsMonitor<AiRuntimeOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiChatOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiFlowDesignerOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiReportGeneratorOptions>>());
        var invalidSettings = new AiChatOptions[]
        {
            new() { MaximumMessages = 0 },
            new() { MaximumMessageCharacters = 99 },
            new() { MaximumDocuments = 21 },
            new() { MaximumDocumentCharacters = 999 },
            new() { MaximumKnowledgeCharacters = 1_000_001 },
            new() { MaximumLocalPromptCharacters = 1_999 },
            new() { MaximumInstructionCharacters = 999 },
            new() { MaximumOutputTokens = 32_001 }
        };

        foreach (var chat in invalidSettings)
        {
            var save = () => configuration.SaveAsync(new SaveAiConfigurationRequest(
                new AiRuntimeOptions(), chat, new AiFlowDesignerOptions(), new AiReportGeneratorOptions(),
                null, false, "admin@example.com"));
            await save.Should().ThrowAsync<ArgumentException>();
        }
        (await context.AiConfiguration.AnyAsync()).Should().BeFalse();
    }

    private sealed class RecordingProvider(IAiConfigurationService configuration) : IAiModelProvider
    {
        public string Key => AiModelProviderKeys.LlamaCpp;
        public AiProviderSettings? Settings { get; private set; }
        public string? Credential { get; private set; }
        public async Task<AiProviderHealthResult> CheckHealthAsync(AiProviderSettings settings,
            CancellationToken cancellationToken = default)
        {
            Settings = settings;
            Credential = await configuration.GetCredentialAsync(settings, cancellationToken);
            return new(true, Key, settings.Model, true, "Available");
        }
        public Task<AiStructuredGenerationResult> GenerateStructuredAsync(AiProviderSettings settings,
            AiStructuredGenerationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiStructuredGenerationResult(Key, settings.Model, request.UserPrompt));
    }

    private static WorkflowDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ServiceProvider CreateOptions()
    {
        var services = new ServiceCollection();
        services.AddOptions<AiRuntimeOptions>().Configure(options =>
        {
            options.Provider = AiModelProviderKeys.Ollama;
            options.Endpoint = "http://127.0.0.1:11434";
            options.Model = "bootstrap-model";
        });
        services.AddOptions<AiFlowDesignerOptions>();
        services.AddOptions<AiChatOptions>();
        services.AddOptions<AiReportGeneratorOptions>();
        return services.BuildServiceProvider();
    }
}
