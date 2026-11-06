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
using System.Text;

namespace TeamHub.Tests;

public sealed class AiConfigurationTests
{
    [Fact]
    public async Task GatewayPrefersPersonalCredentialsAndFallsBackToTheConfiguredM2mCredential()
    {
        await using var context = CreateDbContext();
        using var defaults = CreateOptions();
        var configuration = new AiConfigurationService(context, new EphemeralDataProtectionProvider(),
            defaults.GetRequiredService<IOptionsMonitor<AiRuntimeOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiChatOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiFlowDesignerOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiReportGeneratorOptions>>());
        await configuration.SaveAsync(new(
            new(), new(), new(), new(), null, false, "admin", "m2m-token"));
        var personal = new PersonalGatewayCredentials();
        await personal.SaveTokenAsync("one@example.com", "one-token");
        await personal.SaveTokenAsync("two@example.com", "two-token");
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var accessor = new TeamHubAiProviderCredentialAccessor(http, personal, configuration);
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
        (await accessor.GetCredentialAsync(settings)).Should().Be("m2m-token");
        http.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "one@example.com")]));
        (await accessor.GetCredentialAsync(settings)).Should().Be("m2m-token");
        http.HttpContext = null;
        (await accessor.GetCredentialAsync(settings)).Should().Be("m2m-token");
    }

    [Fact]
    public async Task GatewayRejectsARequestWhenNeitherPersonalNorM2mCredentialsAreAvailable()
    {
        await using var context = CreateDbContext();
        using var defaults = CreateOptions();
        var configuration = new AiConfigurationService(context, new EphemeralDataProtectionProvider(),
            defaults.GetRequiredService<IOptionsMonitor<AiRuntimeOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiChatOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiFlowDesignerOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiReportGeneratorOptions>>());
        var personal = new PersonalGatewayCredentials();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext.User = SignedIn("one@example.com");
        var accessor = new TeamHubAiProviderCredentialAccessor(http, personal, configuration);
        var settings = new AiRuntimeOptions
        {
            Provider = AiModelProviderKeys.PfbAiGateway, OfflineOnly = false,
            Endpoint = "https://gateway.example.test", Model = "gateway-model",
            CredentialEnvironmentVariable = string.Empty
        }.ToProviderSettings();

        var getCredential = async () => await accessor.GetCredentialAsync(settings);
        await getCredential.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*personal gateway token*M2M application key*");
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
        var accessor = new TeamHubAiProviderCredentialAccessor(new HttpContextAccessor(), new PersonalGatewayCredentials(), configuration);
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
            "admin@example.com",
            "pfb-m2m-secret"));

        context.ChangeTracker.Clear();
        var stored = await context.AiConfiguration.SingleAsync();
        stored.SettingsJson.Should().Contain("configured-model");
        stored.SettingsJson.Should().NotContain("super-secret");
        stored.CredentialProtected.Should().NotBeNullOrWhiteSpace().And.NotContain("super-secret");
        stored.PfbM2mCredentialProtected.Should().NotBeNullOrWhiteSpace().And.NotContain("pfb-m2m-secret");

        var current = await service.GetAsync();
        current.Runtime.Provider.Should().Be(AiModelProviderKeys.LlamaCpp);
        current.Chat.Enabled.Should().BeTrue();
        current.Chat.MaximumOutputTokens.Should().Be(6_000);
        current.FlowDesigner.Enabled.Should().BeTrue();
        current.HasCredential.Should().BeTrue();
        current.HasPfbM2mCredential.Should().BeTrue();
        current.UpdatedBy.Should().Be("admin@example.com");
        var credential = await service.GetCredentialAsync(current.Runtime.ToProviderSettings());
        credential.Should().Be("super-secret");
        (await service.GetPfbM2mCredentialAsync()).Should().Be("pfb-m2m-secret");
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
        var columns = await context.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM pragma_table_info('AiConfiguration')")
            .ToListAsync();
        columns.Should().Contain("PfbM2mCredentialProtected");
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

        var originalMeetingNotesPath = (await configuration.GetAsync()).Chat.MeetingNotes.InstructionPath;
        page.Settings.ChatMaximumMessages = 31;
        page.Settings.MeetingNotesInstructionPath = "AI/must-not-change.md";
        (await page.OnPostSaveChatAsync(CancellationToken.None)).Should().BeOfType<RedirectToPageResult>();
        var chatOnlySave = await configuration.GetAsync();
        chatOnlySave.Chat.MaximumMessages.Should().Be(31);
        chatOnlySave.Chat.MeetingNotes.InstructionPath.Should().Be(originalMeetingNotesPath);
        chatOnlySave.FlowDesigner.Enabled.Should().BeTrue();
        chatOnlySave.ReportGenerator.Enabled.Should().BeTrue();

        page.Settings.MeetingNotesEnabled = true;
        page.Settings.MeetingNotesInstructionPath = "AI/custom-meeting-notes.md";
        page.Settings.ChatMaximumMessages = 99;
        (await page.OnPostSaveMeetingNotesAsync(CancellationToken.None)).Should().BeOfType<RedirectToPageResult>();
        var meetingNotesOnlySave = await configuration.GetAsync();
        meetingNotesOnlySave.Chat.MeetingNotes.InstructionPath.Should().Be("AI/custom-meeting-notes.md");
        meetingNotesOnlySave.Chat.MaximumMessages.Should().Be(31);

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

        page.PfbM2mCredential = "configured-m2m-token";
        (await page.OnPostSavePfbM2mCredentialAsync(CancellationToken.None))
            .Should().BeOfType<RedirectToPageResult>();
        (await configuration.GetPfbM2mCredentialAsync()).Should().Be("configured-m2m-token");
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
    public async Task Meeting_notes_configuration_uploads_downloads_and_removes_studio_additional_instructions()
    {
        await using var context = CreateDbContext();
        using var defaults = CreateOptions();
        var configuration = new AiConfigurationService(
            context, new EphemeralDataProtectionProvider(),
            defaults.GetRequiredService<IOptionsMonitor<AiRuntimeOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiChatOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiFlowDesignerOptions>>(),
            defaults.GetRequiredService<IOptionsMonitor<AiReportGeneratorOptions>>());
        var studioDirectory = new ActiveStudioDirectoryStub(
            new StudioDetails { Id = "studio-a", StudioName = "Studio A", IsActive = true });
        var page = new AIModel(configuration, null!, null!, null, studioDirectory)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
            Settings = AiSettingsInput.FromSnapshot(await configuration.GetAsync())
        };
        var content = "# Studio A additional instructions\n\n- Use the studio milestone terminology.";
        var bytes = Encoding.UTF8.GetBytes(content);
        await using var stream = new MemoryStream(bytes);
        page.MeetingNotesTeamsChannels =
        [
            new MeetingNotesTeamsChannelInput
            {
                StudioId = "studio-a",
                StudioName = "Studio A",
                InstructionFile = new FormFile(stream, 0, bytes.Length, "instruction", "StudioA.md")
            }
        ];

        (await page.OnPostSaveMeetingNotesAsync(CancellationToken.None))
            .Should().BeOfType<RedirectToPageResult>();
        var saved = (await configuration.GetAsync()).Chat.MeetingNotes.StudioInstructions["studio-a"];
        saved.FileName.Should().Be("StudioA.md");
        saved.Content.Should().Be(content);
        var download = await page.OnGetDownloadMeetingNotesInstructionAsync("studio-a", CancellationToken.None);
        var file = download.Should().BeOfType<FileContentResult>().Subject;
        file.FileDownloadName.Should().Be("StudioA.md");
        Encoding.UTF8.GetString(file.FileContents).Should().Be(content);

        page.MeetingNotesTeamsChannels =
        [
            new MeetingNotesTeamsChannelInput
            {
                StudioId = "studio-a",
                StudioName = "Studio A",
                RemoveInstruction = true
            }
        ];
        (await page.OnPostSaveMeetingNotesAsync(CancellationToken.None))
            .Should().BeOfType<RedirectToPageResult>();
        (await configuration.GetAsync()).Chat.MeetingNotes.StudioInstructions.Should().BeEmpty();
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

    private sealed class ActiveStudioDirectoryStub(params StudioDetails[] studios) : IStudioDirectoryService
    {
        public Task<IReadOnlyList<StudioDetails>> GetStudiosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StudioDetails>>(studios);

        public Task<StudioDetails?> GetStudioAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(studios.SingleOrDefault(studio =>
                string.Equals(studio.Id, id, StringComparison.OrdinalIgnoreCase)));

        public Task<StudioDetails> SaveStudioAsync(StudioDetails studio, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteStudioAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
