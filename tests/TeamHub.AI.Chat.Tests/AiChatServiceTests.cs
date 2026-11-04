using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.AI.Chat.Contracts;
using TeamHub.AI.Contracts;
using TeamHub.AI.Usage.Contracts;
using Xunit;

namespace TeamHub.AI.Chat.Tests;

public sealed class AiChatServiceTests
{
    [Fact]
    public async Task General_mode_never_calls_application_knowledge_sources()
    {
        var source = new RecordingKnowledgeSource(
            new AiChatSource("private", "Private workflow", "Workflow B secret"));
        var provider = new RecordingProvider();
        await using var services = CreateServices(provider, source);
        var chat = services.GetRequiredService<IAiChatService>();

        var response = await chat.SendAsync(
            User(),
            new AiChatRequest(
                AiChatMode.General,
                "model-a",
                [new AiChatMessage("user", "Summarize my note")],
                [new AiChatDocument("note.txt", "text/plain", "Public personal note")]));

        source.CallCount.Should().Be(0);
        provider.LastRequest!.UserPrompt.Should().Contain("Public personal note");
        provider.LastRequest.UserPrompt.Should().NotContain("Workflow B secret");
        provider.LastRequest.SystemPrompt.Should().Contain("normal general-purpose chatbot");
        response.Answer.Should().Be("Safe answer");
    }

    [Fact]
    public async Task General_mode_without_documents_explicitly_uses_general_knowledge()
    {
        var provider = new RecordingProvider();
        await using var services = CreateServices(provider, new RecordingKnowledgeSource());
        var chat = services.GetRequiredService<IAiChatService>();

        await chat.SendAsync(
            User(),
            new AiChatRequest(
                AiChatMode.General,
                "model-a",
                [new AiChatMessage("user", "What is your name?")]));

        provider.LastRequest!.UserPrompt.Should().Contain(
            "no documents attached; answer ordinary questions using general knowledge");
        provider.LastRequest.SystemPrompt.Should().Contain("User-provided documents are optional, not required");
    }

    [Fact]
    public async Task General_mode_forwards_images_as_multimodal_inputs()
    {
        var provider = new RecordingProvider();
        await using var services = CreateServices(provider, new RecordingKnowledgeSource());
        var chat = services.GetRequiredService<IAiChatService>();
        var image = new AiImageInput("screen.png", "image/png", "iVBORw0KGgo=");

        await chat.SendAsync(
            User(),
            new AiChatRequest(
                AiChatMode.General,
                "model-a",
                [new AiChatMessage("user", "What is visible in this screenshot?")],
                Images: [image]));

        provider.LastRequest!.Images.Should().ContainSingle().Which.Should().Be(image);
        provider.LastRequest.UserPrompt.Should().Contain("screen.png (image/png)");
        provider.LastRequest.UserPrompt.Should().Contain("images are supplied separately");
        provider.LastRequest.SystemPrompt.Should().Contain("inspect and describe their visible content");
    }

    [Fact]
    public async Task Meeting_Notes_mode_uses_only_transcript_content_and_trusted_template()
    {
        var source = new RecordingKnowledgeSource(
            new AiChatSource("private", "Private TeamHub source", "Do not expose this"));
        var provider = new RecordingProvider();
        await using var services = CreateServices(
            provider,
            source,
            meetingNotesInstructions: "Use an Action, Owner, and Due date table.");
        var chat = services.GetRequiredService<IAiChatService>();

        await chat.SendAsync(
            User(),
            new AiChatRequest(
                AiChatMode.MeetingNotes,
                "model-a",
                [new AiChatMessage("user", "Transcript: Mina agreed to send the build on Friday.")],
                Documents: [new AiChatDocument("transcript.md", "text/markdown", "The review starts at 10:00.")],
                StudioId: "studio-a",
                MeetingNotesAdditionalInstructions: "Add a compact Follow-up section."));

        source.CallCount.Should().Be(0);
        provider.LastRequest!.UserPrompt.Should().Contain("Mina agreed to send the build on Friday");
        provider.LastRequest.UserPrompt.Should().Contain("The review starts at 10:00");
        provider.LastRequest.UserPrompt.Should().Contain("Add a compact Follow-up section.");
        provider.LastRequest.UserPrompt.Should().NotContain("Do not expose this");
        provider.LastRequest.SystemPrompt.Should().Contain("only the conversation transcript");
        provider.LastRequest.SystemPrompt.Should().Contain("Use an Action, Owner, and Due date table.");
        provider.LastRequest.SystemPrompt.Should().Contain("dedicated Meeting Notes additional-instructions field");
        provider.LastRequest.SystemPrompt.Should().Contain("assign an owner or due date unless the transcript explicitly supports it");
    }

    [Fact]
    public async Task Disabled_Meeting_Notes_mode_is_rejected_before_generation()
    {
        var provider = new RecordingProvider();
        await using var services = CreateServices(
            provider,
            new RecordingKnowledgeSource(),
            meetingNotesEnabled: false);
        var chat = services.GetRequiredService<IAiChatService>();

        var action = () => chat.SendAsync(
            User(),
            new AiChatRequest(
                AiChatMode.MeetingNotes,
                "model-a",
                [new AiChatMessage("user", "Transcript text")])) ;

        await action.Should().ThrowAsync<AiChatException>().WithMessage("*disabled in AI Services*");
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task TeamHub_mode_uses_only_sources_returned_by_the_authorizing_adapter()
    {
        var source = new RecordingKnowledgeSource(
            new AiChatSource("workflow-a", "Workflow A", "Only authorized workflow A"));
        var provider = new RecordingProvider("workflow-a");
        await using var services = CreateServices(provider, source);
        var chat = services.GetRequiredService<IAiChatService>();

        var response = await chat.SendAsync(
            User(),
            new AiChatRequest(
                AiChatMode.TeamHub,
                "model-b",
                [new AiChatMessage("user", "What is my workflow status?")]));

        source.CallCount.Should().Be(1);
        provider.LastSettings!.Model.Should().Be("model-b");
        provider.LastRequest!.UserPrompt.Should().Contain("Only authorized workflow A");
        response.Citations.Should().ContainSingle()
            .Which.Should().Be(new AiChatCitation("workflow-a", "Workflow A"));
        response.Usage.Should().Be(new AiTokenUsage(80, 20, 100));
    }

    [Fact]
    public async Task TeamHub_mode_executes_one_authorized_tool_and_then_answers_from_its_source()
    {
        var provider = new RecordingProvider(responses:
        [
            """{"action":"tools","answer":"","sourceIds":[],"toolCalls":[{"name":"list_team_members","argumentsJson":"{\"query\":\"engineer\"}"}]}""",
            """{"answer":"Alice is an engineer.","sourceIds":["teamhub-tool-list_team_members"]}"""
        ]);
        var tool = new RecordingToolProvider();
        var usage = new RecordingUsageTracker();
        await using var services = CreateServices(
            provider,
            new RecordingKnowledgeSource(),
            toolProvider: tool,
            usageTracker: usage);
        var chat = services.GetRequiredService<IAiChatService>();

        var response = await chat.SendAsync(
            User(),
            new AiChatRequest(
                AiChatMode.TeamHub,
                "model-a",
                [new AiChatMessage("user", "List engineers in the team")])) ;

        tool.CallCount.Should().Be(1);
        tool.LastToolName.Should().Be("list_team_members");
        tool.LastArguments.GetProperty("query").GetString().Should().Be("engineer");
        provider.Requests.Should().HaveCount(2);
        provider.Requests[0].SystemPrompt.Should().Contain("<teamhub_tools>");
        provider.Requests[1].UserPrompt.Should().Contain("Alice");
        response.Answer.Should().Be("Alice is an engineer.");
        response.Citations.Should().ContainSingle().Which.Id.Should().Be("teamhub-tool-list_team_members");
        response.Usage.Should().Be(new AiTokenUsage(160, 40, 200));
        usage.Invocations.Should().HaveCount(2);
        usage.Invocations.Select(item => item.Operation.OperationId).Distinct().Should().ContainSingle();
        usage.Invocations.Select(item => item.Invocation.ResourceId)
            .Should().Equal("ToolPlanning", "list_team_members");
    }

    [Fact]
    public async Task TeamHub_mode_executes_multiple_authorized_tools_before_one_final_answer()
    {
        var provider = new RecordingProvider(responses:
        [
            """{"action":"tools","answer":"","sourceIds":[],"toolCalls":[{"name":"list_team_members","argumentsJson":"{\"query\":\"Alice\"}"},{"name":"get_studio_support_overview","argumentsJson":"{\"studio\":\"Example Studio\"}"}]}""",
            """{"answer":"Alice supports the Example Studio Alpha milestone.","sourceIds":["teamhub-tool-list_team_members","teamhub-tool-get_studio_support_overview"]}"""
        ]);
        var tool = new RecordingToolProvider();
        var usage = new RecordingUsageTracker();
        var toolActivity = new RecordingToolActivityTracker();
        await using var services = CreateServices(
            provider,
            new RecordingKnowledgeSource(),
            toolProvider: tool,
            usageTracker: usage,
            toolActivityTracker: toolActivity);
        var chat = services.GetRequiredService<IAiChatService>();

        var response = await chat.SendAsync(
            User(),
            new AiChatRequest(
                AiChatMode.TeamHub,
                "model-a",
                [new AiChatMessage("user", "Who supports Example Studio and what is its milestone status?")]));

        tool.Calls.Select(item => item.Name)
            .Should().Equal("list_team_members", "get_studio_support_overview");
        provider.Requests.Should().HaveCount(2);
        provider.Requests[1].UserPrompt.Should().Contain("Alice");
        provider.Requests[1].UserPrompt.Should().Contain("Alpha milestone");
        response.Answer.Should().Contain("Example Studio Alpha milestone");
        response.Citations.Select(item => item.Id).Should().Equal(
            "teamhub-tool-list_team_members",
            "teamhub-tool-get_studio_support_overview");
        response.Usage.Should().Be(new AiTokenUsage(160, 40, 200));
        usage.Invocations.Should().HaveCount(2);
        usage.Invocations[1].Invocation.ResourceType.Should().Be("AiTools");
        usage.Invocations[1].Invocation.ResourceId.Should().Be(
            "list_team_members,get_studio_support_overview");
        toolActivity.Activities.Should().HaveCount(2);
        toolActivity.Activities[1].Activity.Should().Be(new AiToolActivityContext(
            "get_studio_support_overview", "Studio", "Example Studio"));
        toolActivity.Activities.Select(item => item.Operation.OperationId).Distinct()
            .Should().ContainSingle().Which.Should().Be(usage.Invocations[0].Operation.OperationId);
    }

    [Fact]
    public async Task TeamHub_mode_rejects_more_than_four_tool_calls_before_execution()
    {
        var provider = new RecordingProvider(responses:
        [
            """{"action":"tools","answer":"","sourceIds":[],"toolCalls":[{"name":"list_team_members","argumentsJson":"{\"query\":\"one\"}"},{"name":"list_team_members","argumentsJson":"{\"query\":\"two\"}"},{"name":"list_team_members","argumentsJson":"{\"query\":\"three\"}"},{"name":"list_team_members","argumentsJson":"{\"query\":\"four\"}"},{"name":"list_team_members","argumentsJson":"{\"query\":\"five\"}"}]}"""
        ]);
        var tool = new RecordingToolProvider();
        await using var services = CreateServices(
            provider,
            new RecordingKnowledgeSource(),
            toolProvider: tool);
        var chat = services.GetRequiredService<IAiChatService>();

        var action = () => chat.SendAsync(
            User(),
            new AiChatRequest(
                AiChatMode.TeamHub,
                "model-a",
                [new AiChatMessage("user", "Run too many lookups")])) ;

        await action.Should().ThrowAsync<AiChatException>().WithMessage("*between 1 and 4*");
        tool.CallCount.Should().Be(0);
        provider.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData("Generate an image of a flower")]
    [InlineData("Please draw a company logo")]
    [InlineData("Make a photo of a mountain")]
    public async Task TeamHub_mode_rejects_creative_image_generation(string question)
    {
        var provider = new RecordingProvider();
        var source = new RecordingKnowledgeSource(
            new AiChatSource("teamhub", "TeamHub source", "Authorized information"));
        await using var services = CreateServices(provider, source);
        var chat = services.GetRequiredService<IAiChatService>();

        var response = await chat.SendAsync(
            User(),
            new AiChatRequest(
                AiChatMode.TeamHub,
                "model-a",
                [new AiChatMessage("user", question)]));

        response.Answer.Should().Contain("not available in TeamHub mode");
        response.Answer.Should().Contain("General mode");
        response.Citations.Should().BeEmpty();
        source.CallCount.Should().Be(0);
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task TeamHub_mode_allows_authorized_data_visualization_requests()
    {
        var provider = new RecordingProvider();
        var source = new RecordingKnowledgeSource(
            new AiChatSource("support", "Support Summary", "Studio A has 3 active support items"));
        await using var services = CreateServices(provider, source);
        var chat = services.GetRequiredService<IAiChatService>();

        await chat.SendAsync(
            User(),
            new AiChatRequest(
                AiChatMode.TeamHub,
                "model-a",
                [new AiChatMessage("user", "Generate an image chart of current support activity")]));

        source.CallCount.Should().Be(1);
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.SystemPrompt.Should().Contain(
            "chart or diagram only when it directly visualizes information from the authorized TeamHub sources");
    }

    [Fact]
    public async Task Trusted_playbook_is_added_only_to_TeamHub_mode()
    {
        var provider = new RecordingProvider();
        await using var services = CreateServices(
            provider,
            new RecordingKnowledgeSource(),
            teamHubInstructions: "## Test routing\nUse the test source for project status.");
        var chat = services.GetRequiredService<IAiChatService>();

        await chat.SendAsync(
            User(),
            new AiChatRequest(
                AiChatMode.TeamHub,
                "model-a",
                [new AiChatMessage("user", "What is the project status?")]));

        provider.LastRequest!.SystemPrompt.Should().Contain("<teamhub_playbook>");
        provider.LastRequest.SystemPrompt.Should().Contain("Use the test source for project status.");

        await chat.SendAsync(
            User(),
            new AiChatRequest(
                AiChatMode.General,
                "model-a",
                [new AiChatMessage("user", "What is a project?")]));

        provider.LastRequest!.SystemPrompt.Should().NotContain("<teamhub_playbook>");
        provider.LastRequest.SystemPrompt.Should().NotContain("Use the test source for project status.");
    }

    [Fact]
    public async Task A_model_not_in_the_gateway_catalog_is_rejected_before_generation()
    {
        var provider = new RecordingProvider();
        await using var services = CreateServices(provider, new RecordingKnowledgeSource());
        var chat = services.GetRequiredService<IAiChatService>();

        var action = () => chat.SendAsync(
            User(),
            new AiChatRequest(
                AiChatMode.General,
                "invented-model",
                [new AiChatMessage("user", "Hello")]));

        await action.Should().ThrowAsync<AiChatException>()
            .WithMessage("*currently available*");
        provider.LastRequest.Should().BeNull();
    }

    [Theory]
    [InlineData(AiModelProviderKeys.Ollama)]
    [InlineData(AiModelProviderKeys.LlamaCpp)]
    [InlineData(AiModelProviderKeys.OpenAiCompatible)]
    public async Task Chat_uses_the_provider_selected_in_AI_services(string providerKey)
    {
        var provider = new RecordingProvider(providerKey: providerKey);
        await using var services = CreateServices(provider, new RecordingKnowledgeSource());
        var chat = services.GetRequiredService<IAiChatService>();

        await chat.SendAsync(
            User(),
            new AiChatRequest(
                AiChatMode.General,
                "model-a",
                [new AiChatMessage("user", "Hello")]));

        provider.LastSettings!.Provider.Should().Be(providerKey);
        provider.LastSettings.Model.Should().Be("model-a");
    }

    [Fact]
    public async Task Local_provider_uses_its_configured_model_when_discovery_is_unavailable()
    {
        var provider = new RecordingProvider(
            providerKey: AiModelProviderKeys.Ollama,
            failCatalog: true);
        await using var services = CreateServices(provider, new RecordingKnowledgeSource());
        var chat = services.GetRequiredService<IAiChatService>();

        var models = await chat.GetModelsAsync();
        var response = await chat.SendAsync(
            User(),
            new AiChatRequest(
                AiChatMode.General,
                "model-a",
                [new AiChatMessage("user", "Hello")]));

        models.Should().ContainSingle().Which.Id.Should().Be("model-a");
        response.Model.Should().Be("model-a");
        provider.LastRequest.Should().NotBeNull();
    }

    [Fact]
    public async Task Local_provider_prompt_is_bounded_and_keeps_the_latest_question()
    {
        var provider = new RecordingProvider(providerKey: AiModelProviderKeys.LlamaCpp);
        var source = new RecordingKnowledgeSource(
            new AiChatSource("large", "Large source", new string('x', 30_000)));
        await using var services = CreateServices(provider, source);
        var chat = services.GetRequiredService<IAiChatService>();

        await chat.SendAsync(
            User(),
            new AiChatRequest(
                AiChatMode.TeamHub,
                "model-a",
                [new AiChatMessage("user", "What is my latest project status?")]));

        provider.LastRequest!.UserPrompt.Length.Should().BeLessThanOrEqualTo(18_000);
        provider.LastRequest.UserPrompt.Should().EndWith("What is my latest project status?");
    }

    [Fact]
    public async Task Chat_disabled_in_AI_services_blocks_model_discovery_and_generation()
    {
        var provider = new RecordingProvider();
        await using var services = CreateServices(
            provider,
            new RecordingKnowledgeSource(),
            chatEnabled: false);
        var chat = services.GetRequiredService<IAiChatService>();

        var models = () => chat.GetModelsAsync();
        var send = () => chat.SendAsync(
            User(),
            new AiChatRequest(
                AiChatMode.General,
                "model-a",
                [new AiChatMessage("user", "Hello")]));

        await models.Should().ThrowAsync<AiChatException>().WithMessage("*disabled in AI Services*");
        await send.Should().ThrowAsync<AiChatException>().WithMessage("*disabled in AI Services*");
        provider.LastRequest.Should().BeNull();
    }

    private static ServiceProvider CreateServices(
        RecordingProvider provider,
        RecordingKnowledgeSource source,
        bool chatEnabled = true,
        string? teamHubInstructions = null,
        string? meetingNotesInstructions = null,
        bool meetingNotesEnabled = true,
        IAiChatToolProvider? toolProvider = null,
        IAiUsageTracker? usageTracker = null,
        IAiToolActivityTracker? toolActivityTracker = null)
    {
        var services = new ServiceCollection();
        services.AddAiChat(new ConfigurationBuilder().Build());
        services.AddSingleton<IAiConfigurationService>(
            new TestConfigurationService(provider.Key, chatEnabled, meetingNotesEnabled));
        services.AddSingleton<IAiModelProviderRegistry>(new TestProviderRegistry(provider));
        services.AddSingleton<IAiChatKnowledgeSource>(source);
        if (toolProvider is not null) services.AddSingleton(toolProvider);
        if (usageTracker is not null) services.AddSingleton(usageTracker);
        if (toolActivityTracker is not null) services.AddSingleton(toolActivityTracker);
        if (teamHubInstructions is not null)
            services.AddSingleton<IAiChatInstructionProvider>(
                new TestInstructionProvider(teamHubInstructions));
        if (meetingNotesInstructions is not null)
            services.AddSingleton<IAiMeetingNotesInstructionProvider>(
                new TestMeetingNotesInstructionProvider(meetingNotesInstructions));
        return services.BuildServiceProvider();
    }

    private static AiChatUserContext User() => new(
        "user.a@example.com", "User A", "Registered");

    private sealed class RecordingKnowledgeSource(params AiChatSource[] sources) : IAiChatKnowledgeSource
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<AiChatSource>> SearchAsync(
            AiChatUserContext user,
            string question,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<AiChatSource>>(sources);
        }
    }

    private sealed class TestInstructionProvider(string content) : IAiChatInstructionProvider
    {
        public Task<string> GetTeamHubInstructionsAsync(
            string question,
            IReadOnlyList<AiChatSource> sources,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(content);
    }

    private sealed class TestMeetingNotesInstructionProvider(string content)
        : IAiMeetingNotesInstructionProvider
    {
        public Task<string> GetInstructionsAsync(
            string? studioId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(content);
    }

    private sealed class RecordingProvider(
        string? citation = null,
        string providerKey = AiModelProviderKeys.PfbAiGateway,
        bool failCatalog = false,
        IReadOnlyList<string>? responses = null)
        : IAiModelProvider, IAiModelCatalogProvider
    {
        public string Key => providerKey;
        public AiProviderSettings? LastSettings { get; private set; }
        public AiStructuredGenerationRequest? LastRequest { get; private set; }
        public List<AiStructuredGenerationRequest> Requests { get; } = [];

        public Task<IReadOnlyList<AiModelCatalogItem>> GetModelsAsync(
            AiProviderSettings settings,
            CancellationToken cancellationToken = default)
        {
            if (failCatalog) throw new AiModelProviderException("Catalog unavailable.");
            return Task.FromResult<IReadOnlyList<AiModelCatalogItem>>([
                new("model-a", "chat", "low", []),
                new("model-b", "chat", "medium", [])
            ]);
        }

        public Task<AiProviderHealthResult> CheckHealthAsync(
            AiProviderSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiProviderHealthResult(true, Key, settings.Model, true, "OK"));

        public Task<AiStructuredGenerationResult> GenerateStructuredAsync(
            AiProviderSettings settings,
            AiStructuredGenerationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastSettings = settings;
            LastRequest = request;
            Requests.Add(request);
            var sourceIds = citation is null ? "[]" : JsonSerializer.Serialize(new[] { citation });
            return Task.FromResult(new AiStructuredGenerationResult(
                Key,
                settings.Model,
                responses is not null && Requests.Count <= responses.Count
                    ? responses[Requests.Count - 1]
                    : $$"""{"answer":"Safe answer","sourceIds":{{sourceIds}}}""",
                "request-1",
                new AiTokenUsage(80, 20, 100)));
        }
    }

    private sealed class RecordingToolProvider : IAiChatToolProvider
    {
        public IReadOnlyList<AiChatToolDefinition> Definitions { get; } =
        [
            new("list_team_members", "List team members", "{\"query\":\"optional\"}"),
            new("get_studio_support_overview", "Get studio support", "{\"studio\":\"required\"}")
        ];
        public int CallCount { get; private set; }
        public string? LastToolName { get; private set; }
        public JsonElement LastArguments { get; private set; }
        public List<(string Name, JsonElement Arguments)> Calls { get; } = [];

        public Task<AiChatSource> ExecuteAsync(
            AiChatUserContext user,
            string toolName,
            JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastToolName = toolName;
            LastArguments = arguments.Clone();
            Calls.Add((toolName, arguments.Clone()));
            return Task.FromResult(toolName switch
            {
                "list_team_members" => new AiChatSource(
                    "teamhub-tool-list_team_members",
                    "TeamHub team members",
                    "[{\"employeeName\":\"Alice\",\"role\":\"Engineer\"}]"),
                "get_studio_support_overview" => new AiChatSource(
                    "teamhub-tool-get_studio_support_overview",
                    "Studio support overview",
                    "{\"studio\":\"Example Studio\",\"milestone\":\"Alpha milestone\"}"),
                _ => throw new InvalidOperationException("Unexpected test tool.")
            });
        }
    }

    private sealed class RecordingUsageTracker : IAiUsageTracker
    {
        public List<(AiUsageOperationContext Operation, AiUsageInvocationContext Invocation)> Invocations { get; } = [];

        public async Task<AiStructuredGenerationResult> TrackGenerationAsync(
            AiUsageOperationContext operation,
            AiUsageInvocationContext invocation,
            string expectedProvider,
            string expectedModel,
            Func<CancellationToken, Task<AiStructuredGenerationResult>> generate,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add((operation, invocation));
            return await generate(cancellationToken);
        }
    }

    private sealed class RecordingToolActivityTracker : IAiToolActivityTracker
    {
        public List<(AiUsageOperationContext Operation, AiToolActivityContext Activity)> Activities { get; } = [];

        public Task<T> TrackAsync<T>(
            AiUsageOperationContext operation,
            AiToolActivityContext activity,
            Func<CancellationToken, Task<T>> execute,
            CancellationToken cancellationToken = default)
        {
            Activities.Add((operation, activity));
            return execute(cancellationToken);
        }
    }

    private sealed class TestProviderRegistry(IAiModelProvider provider) : IAiModelProviderRegistry
    {
        public IAiModelProvider GetRequired(string providerKey) => provider;
        public IReadOnlyList<string> ProviderKeys => [provider.Key];
    }

    private sealed class TestConfigurationService(
        string providerKey,
        bool chatEnabled,
        bool meetingNotesEnabled) : IAiConfigurationService
    {
        public Task<AiConfigurationSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiConfigurationSnapshot
            {
                Runtime = new AiRuntimeOptions
                {
                    Provider = providerKey,
                    OfflineOnly = false,
                    Temperature = 0.2,
                    TimeoutSeconds = 30,
                    Providers = new Dictionary<string, AiProviderProfileOptions>(StringComparer.OrdinalIgnoreCase)
                    {
                        [providerKey] = new()
                        {
                            Endpoint = "https://gateway.example",
                            Model = "model-a",
                            ApiMode = "CommonChat"
                        }
                    }
                },
                Chat = new AiChatOptions
                {
                    Enabled = chatEnabled,
                    MeetingNotes = new AiMeetingNotesOptions { Enabled = meetingNotesEnabled }
                }
            });

        public Task SaveAsync(
            SaveAiConfigurationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<string?> GetCredentialAsync(
            AiProviderSettings settings,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>("unused");
    }
}
