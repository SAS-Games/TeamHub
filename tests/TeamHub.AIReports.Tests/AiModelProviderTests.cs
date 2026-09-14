using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.AIReports.Contracts;
using TeamHub.AIReports.ModelProviders;

namespace TeamHub.AIReports.Tests;

public sealed class AiModelProviderTests
{
    [Fact]
    public void Registry_selects_provider_case_insensitively()
    {
        using var services = CreateServices().BuildServiceProvider();
        var registry = services.GetRequiredService<IAiModelProviderRegistry>();

        registry.GetRequired("ollama").Key.Should().Be(AiModelProviderKeys.Ollama);
        registry.GetRequired("OPENAICOMPATIBLE").Key.Should().Be(AiModelProviderKeys.OpenAiCompatible);
    }

    [Fact]
    public async Task Ollama_health_check_uses_open_ai_compatible_model_list()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            request.RequestUri.Should().Be("http://127.0.0.1:11434/v1/models");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"llama3.2:latest"}]}""", Encoding.UTF8, "application/json")
            };
        });
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
    public async Task Disabled_runtime_rejects_model_calls_without_network_access()
    {
        using var services = CreateServices().BuildServiceProvider();
        var runtime = services.GetRequiredService<IAiReportModelService>();

        runtime.IsEnabled.Should().BeFalse();
        var action = () => runtime.CheckHealthAsync();
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*disabled*");
    }

    private static ServiceCollection CreateServices()
    {
        var values = new Dictionary<string, string?>
        {
            ["AIReports:Enabled"] = "false",
            ["ConnectionStrings:AiReportsDb"] = "Data Source=:memory:"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiReports(configuration);
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
