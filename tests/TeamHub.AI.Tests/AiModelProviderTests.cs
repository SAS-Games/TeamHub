using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TeamHub.AI.Contracts;
using TeamHub.AI.ModelProviders;

namespace TeamHub.AI.Tests;

public sealed class AiModelProviderTests
{
    [Fact]
    public async Task Missing_ai_configuration_uses_safe_built_in_defaults()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTeamHubAi(configuration);
        using var provider = services.BuildServiceProvider();

        var snapshot = await provider.GetRequiredService<IAiConfigurationService>().GetAsync();

        snapshot.Runtime.Provider.Should().Be(AiModelProviderKeys.Ollama);
        snapshot.Runtime.ToProviderSettings().Endpoint.Should().Be("http://127.0.0.1:11434/");
        snapshot.Runtime.ToProviderSettings().Model.Should().Be("qwen3:8b");
        snapshot.Runtime.OfflineOnly.Should().BeTrue();
        snapshot.FlowDesigner.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Registry_selects_provider_case_insensitively()
    {
        using var services = CreateServices().BuildServiceProvider();
        var registry = services.GetRequiredService<IAiModelProviderRegistry>();

        registry.GetRequired("ollama").Key.Should().Be(AiModelProviderKeys.Ollama);
        registry.GetRequired("LLAMACPP").Key.Should().Be(AiModelProviderKeys.LlamaCpp);
        registry.GetRequired("OPENAICOMPATIBLE").Key.Should().Be(AiModelProviderKeys.OpenAiCompatible);
        registry.GetRequired("PFBAIGATEWAY").Key.Should().Be(AiModelProviderKeys.PfbAiGateway);
    }

    [Fact]
    public async Task Ollama_health_check_uses_open_ai_compatible_model_list()
    {
        var handler = CreateHealthyHandler(
            "http://127.0.0.1:11434/v1/models",
            "llama3.2:latest");
        var provider = new OllamaModelProvider(
            new StubHttpClientFactory(handler),
            new NullCredentialAccessor());
        var settings = new AiProviderSettings(
            AiModelProviderKeys.Ollama,
            new Uri("http://127.0.0.1:11434"),
            "llama3.2:latest",
            0,
            TimeSpan.FromSeconds(5),
            string.Empty);

        var result = await provider.CheckHealthAsync(settings);

        result.IsAvailable.Should().BeTrue();
        result.IsModelAvailable.Should().BeTrue();
        result.Provider.Should().Be(AiModelProviderKeys.Ollama);
    }

    [Fact]
    public async Task Runtime_uses_the_provider_and_model_selected_in_shared_configuration()
    {
        var handler = CreateHealthyHandler(
            "http://127.0.0.1:11434/v1/models",
            "llama3.2:latest");
        using var services = CreateServices(handler).BuildServiceProvider();
        var runtime = services.GetRequiredService<IAiModelService>();

        var result = await runtime.CheckHealthAsync();

        result.Provider.Should().Be(AiModelProviderKeys.Ollama);
        result.Model.Should().Be("llama3.2:latest");
        result.IsModelAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task Offline_only_mode_rejects_a_remote_endpoint_before_sending_a_request()
    {
        var handler = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("No HTTP request should be sent."));
        using var services = CreateServices(
            handler,
            new Dictionary<string, string?>
            {
                ["AI:Providers:Ollama:Endpoint"] = "https://example.com",
                ["AI:OfflineOnly"] = "true"
            }).BuildServiceProvider();
        var runtime = services.GetRequiredService<IAiModelService>();

        var action = () => runtime.CheckHealthAsync();

        await action.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*AI:OfflineOnly*");
    }

    [Fact]
    public async Task Llama_cpp_profile_is_selected_by_changing_only_the_provider()
    {
        var handler = CreateHealthyHandler(
            "http://127.0.0.1:8080/v1/models",
            "qwen3:8b");
        using var services = CreateServices(
            handler,
            new Dictionary<string, string?>
            {
                ["AI:Provider"] = AiModelProviderKeys.LlamaCpp
            }).BuildServiceProvider();
        var runtime = services.GetRequiredService<IAiModelService>();

        var result = await runtime.CheckHealthAsync();

        result.IsAvailable.Should().BeTrue();
        result.Provider.Should().Be(AiModelProviderKeys.LlamaCpp);
        result.Model.Should().Be("qwen3:8b");
    }

    [Fact]
    public async Task Generic_open_ai_provider_can_use_legacy_top_level_settings()
    {
        var handler = CreateHealthyHandler(
            "http://localhost:1234/v1/models",
            "local-model");
        using var services = CreateServices(
            handler,
            new Dictionary<string, string?>
            {
                ["AI:Provider"] = AiModelProviderKeys.OpenAiCompatible,
                ["AI:Endpoint"] = "http://localhost:1234",
                ["AI:Model"] = "local-model"
            }).BuildServiceProvider();
        var runtime = services.GetRequiredService<IAiModelService>();

        var result = await runtime.CheckHealthAsync();

        result.IsAvailable.Should().BeTrue();
        result.Provider.Should().Be(AiModelProviderKeys.OpenAiCompatible);
    }

    [Fact]
    public async Task Pfb_gateway_health_uses_portal_catalog_and_jwt_bearer_authentication()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            request.RequestUri.Should().Be("https://ai-gateway.example/portal/pfb/models");
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            request.Headers.Authorization.Parameter.Should().Be("test-jwt");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"models":[{"id":"gpt-5-nano","cost_tier":"low"}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var provider = new PfbAiGatewayModelProvider(
            new StubHttpClientFactory(handler),
            new TokenCredentialAccessor("test-jwt"));

        var result = await provider.CheckHealthAsync(PfbSettings());

        result.IsAvailable.Should().BeTrue();
        result.IsModelAvailable.Should().BeTrue();
        result.Provider.Should().Be(AiModelProviderKeys.PfbAiGateway);
    }

    [Fact]
    public async Task Pfb_gateway_model_catalog_includes_metadata_for_the_configuration_picker()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse(
            """{"models":[{"id":"gpt-5-nano","cost_tier":"low","type":"chat","endpoints":["/pfb/common/v1/chat/completions"]},{"model_id":"claude-sonnet","costTier":"high","endpoint":"/pfb/common/v1/chat/completions"}]}"""));
        var provider = new PfbAiGatewayModelProvider(
            new StubHttpClientFactory(handler),
            new TokenCredentialAccessor("test-jwt"));

        var models = await provider.GetModelsAsync(PfbSettings());

        models.Select(model => model.Id).Should().Equal("claude-sonnet", "gpt-5-nano");
        models.Single(model => model.Id == "gpt-5-nano").CostTier.Should().Be("low");
        models.Single(model => model.Id == "gpt-5-nano").Endpoints
            .Should().ContainSingle("/pfb/common/v1/chat/completions");
        models.Single(model => model.Id == "claude-sonnet").CostTier.Should().Be("high");
    }

    [Fact]
    public async Task Pfb_gateway_common_chat_uses_gateway_route_and_returns_structured_content()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            request.RequestUri.Should().Be("https://ai-gateway.example/pfb/common/v1/chat/completions");
            request.Headers.Authorization!.Parameter.Should().Be("test-jwt");
            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            payload.RootElement.GetProperty("model").GetString().Should().Be("gpt-5-nano");
            payload.RootElement.TryGetProperty("response_format", out _).Should().BeFalse();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"request-1","choices":[{"message":{"role":"assistant","content":"```json\n{\"answer\":true}\n```"}}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var provider = new PfbAiGatewayModelProvider(
            new StubHttpClientFactory(handler),
            new TokenCredentialAccessor("test-jwt"));
        using var schema = JsonDocument.Parse("""{"type":"object"}""");

        var result = await provider.GenerateStructuredAsync(
            PfbSettings(),
            new AiStructuredGenerationRequest(
                "Return JSON.",
                "Create the result.",
                schema.RootElement.Clone(),
                1000));

        result.Content.Should().Be("{\"answer\":true}");
        result.Provider.Should().Be(AiModelProviderKeys.PfbAiGateway);
    }

    [Fact]
    public async Task Pfb_gateway_open_ai_chat_sends_the_json_schema()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            request.RequestUri.Should().Be("https://ai-gateway.example/pfb/openai/v1/chat/completions");
            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            payload.RootElement.GetProperty("response_format").GetProperty("type").GetString()
                .Should().Be("json_schema");
            return JsonResponse("""{"choices":[{"message":{"role":"assistant","content":"{\"answer\":true}"}}]}""");
        });
        var provider = new PfbAiGatewayModelProvider(
            new StubHttpClientFactory(handler),
            new TokenCredentialAccessor("test-jwt"));
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var settings = PfbSettings() with { ApiMode = PfbAiGatewayModelProvider.OpenAiChatMode };

        var result = await provider.GenerateStructuredAsync(
            settings,
            new AiStructuredGenerationRequest("Return JSON.", "Create it.", schema.RootElement.Clone()));

        result.Content.Should().Be("{\"answer\":true}");
    }

    [Fact]
    public async Task Pfb_gateway_open_ai_responses_reads_output_text()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            request.RequestUri.Should().Be("https://ai-gateway.example/pfb/openai/v1/responses");
            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            payload.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString()
                .Should().Be("json_schema");
            return JsonResponse("""{"id":"response-1","output_text":"{\"answer\":true}"}""");
        });
        var provider = new PfbAiGatewayModelProvider(
            new StubHttpClientFactory(handler),
            new TokenCredentialAccessor("test-jwt"));
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var settings = PfbSettings() with { ApiMode = PfbAiGatewayModelProvider.OpenAiResponsesMode };

        var result = await provider.GenerateStructuredAsync(
            settings,
            new AiStructuredGenerationRequest("Return JSON.", "Create it.", schema.RootElement.Clone()));

        result.Content.Should().Be("{\"answer\":true}");
    }

    private static StubHttpMessageHandler CreateHealthyHandler(
        string expectedModelsEndpoint,
        string model) => new(request =>
    {
        request.RequestUri.Should().Be(expectedModelsEndpoint);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"data":[{"id":"{{model}}"}]}""",
                Encoding.UTF8,
                "application/json")
        };
    });

    private static AiProviderSettings PfbSettings() => new(
        AiModelProviderKeys.PfbAiGateway,
        new Uri("https://ai-gateway.example"),
        "gpt-5-nano",
        0.2,
        TimeSpan.FromSeconds(5),
        "TEAMHUB_AI_PROVIDER_API_KEY",
        PfbAiGatewayModelProvider.CommonChatMode);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static ServiceCollection CreateServices(
        HttpMessageHandler? handler = null,
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["AI:Provider"] = AiModelProviderKeys.Ollama,
            ["AI:Endpoint"] = "http://127.0.0.1:11434",
            ["AI:Model"] = "llama3.2:latest",
            ["AI:Providers:Ollama:Endpoint"] = "http://127.0.0.1:11434",
            ["AI:Providers:Ollama:Model"] = "llama3.2:latest",
            ["AI:Providers:LlamaCpp:Endpoint"] = "http://127.0.0.1:8080",
            ["AI:Providers:LlamaCpp:Model"] = "qwen3:8b",
            ["AI:OfflineOnly"] = "true",
            ["AI:TimeoutSeconds"] = "5"
        };
        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                values[key] = value;
            }
        }
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTeamHubAi(configuration);
        if (handler is not null)
        {
            services.AddHttpClient("TeamHub.AI")
                .ConfigurePrimaryHttpMessageHandler(() => handler);
        }

        return services;
    }

    private sealed class NullCredentialAccessor : IAiProviderCredentialAccessor
    {
        public ValueTask<string?> GetCredentialAsync(
            AiProviderSettings settings,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);
    }

    private sealed class TokenCredentialAccessor(string token) : IAiProviderCredentialAccessor
    {
        public ValueTask<string?> GetCredentialAsync(
            AiProviderSettings settings,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(token);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responseFactory(request));
    }
}
