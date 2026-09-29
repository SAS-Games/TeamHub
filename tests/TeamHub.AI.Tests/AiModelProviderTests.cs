using System.Net;
using System.Text;
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
    public void Registry_selects_provider_case_insensitively()
    {
        using var services = CreateServices().BuildServiceProvider();
        var registry = services.GetRequiredService<IAiModelProviderRegistry>();

        registry.GetRequired("ollama").Key.Should().Be(AiModelProviderKeys.Ollama);
        registry.GetRequired("LLAMACPP").Key.Should().Be(AiModelProviderKeys.LlamaCpp);
        registry.GetRequired("OPENAICOMPATIBLE").Key.Should().Be(AiModelProviderKeys.OpenAiCompatible);
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
