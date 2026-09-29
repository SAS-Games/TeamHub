using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using TeamHub.AI.Contracts;
using TeamHub.AI.ModelProviders;

namespace TeamHub.AI.Tests;

public sealed class StructuredGenerationTests
{
    [Fact]
    public async Task Provider_sends_open_ai_json_schema_contract_and_returns_content()
    {
        JsonDocument? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            request.RequestUri.Should().Be("http://localhost:11434/v1/chat/completions");
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedRequest = JsonDocument.Parse(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"request-1","choices":[{"message":{"role":"assistant","content":"{\"rows\":[]}"}}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var provider = new OllamaModelProvider(
            new StubHttpClientFactory(handler),
            new NullCredentialAccessor());
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"rows":{"type":"array"}},"required":["rows"]}""");
        var request = new AiStructuredGenerationRequest(
            "Preserve facts.",
            "Rewrite this report.",
            schema.RootElement.Clone(),
            500);
        var settings = new AiProviderSettings(
            AiModelProviderKeys.Ollama,
            new Uri("http://localhost:11434"),
            "llama3.2:latest",
            0,
            TimeSpan.FromSeconds(5),
            string.Empty);

        var result = await provider.GenerateStructuredAsync(settings, request);

        result.Content.Should().Be("{\"rows\":[]}");
        result.RequestId.Should().Be("request-1");
        var root = capturedRequest!.RootElement;
        root.GetProperty("model").GetString().Should().Be("llama3.2:latest");
        root.GetProperty("response_format").GetProperty("type").GetString().Should().Be("json_schema");
        root.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean().Should().BeTrue();
        root.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema")
            .GetProperty("properties").TryGetProperty("rows", out _).Should().BeTrue();
        capturedRequest.Dispose();
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
