using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeamHub.AI.Contracts;

namespace TeamHub.AI.ModelProviders;

public class OpenAiCompatibleModelProvider(
    IHttpClientFactory httpClientFactory,
    IAiProviderCredentialAccessor credentials) : IAiModelProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public virtual string Key => AiModelProviderKeys.OpenAiCompatible;

    public async Task<AiProviderHealthResult> CheckHealthAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CreateTimeout(settings, cancellationToken);
            using var client = await CreateClientAsync(settings, timeout.Token);
            using var response = await client.GetAsync("models", timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new(false, Key, settings.Model, false,
                    $"Provider returned HTTP {(int)response.StatusCode} while checking available models.");
            }

            var models = await response.Content.ReadFromJsonAsync<ModelListResponse>(SerializerOptions, timeout.Token);
            var modelAvailable = models?.Data?.Any(model =>
                string.Equals(model.Id, settings.Model, StringComparison.OrdinalIgnoreCase)) == true;

            return modelAvailable
                ? new(true, Key, settings.Model, true, "Provider and configured model are available.")
                : new(true, Key, settings.Model, false, "Provider is available, but the configured model was not found.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, Key, settings.Model, false, "Provider health check timed out.");
        }
        catch (HttpRequestException)
        {
            return new(false, Key, settings.Model, false, "Provider endpoint could not be reached.");
        }
        catch (JsonException)
        {
            return new(false, Key, settings.Model, false, "Provider returned an invalid model-list response.");
        }
    }

    public async Task<AiStructuredGenerationResult> GenerateStructuredAsync(
        AiProviderSettings settings,
        AiStructuredGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SystemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserPrompt);
        if (request.ResponseSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ArgumentException("A JSON response schema is required.", nameof(request));
        }

        try
        {
            using var timeout = CreateTimeout(settings, cancellationToken);
            using var client = await CreateClientAsync(settings, timeout.Token);
            var payload = new ChatCompletionRequest(
                settings.Model,
                [
                    new("system", request.SystemPrompt),
                    new("user", request.UserPrompt)
                ],
                settings.Temperature,
                request.MaximumOutputTokens,
                new("json_schema", new("teamhub_ai_report", true, request.ResponseSchema)));

            using var response = await client.PostAsJsonAsync("chat/completions", payload, SerializerOptions, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new AiModelProviderException(
                    $"AI provider returned HTTP {(int)response.StatusCode} while generating the report.");
            }

            var completion = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(SerializerOptions, timeout.Token)
                ?? throw new AiModelProviderException("AI provider returned an empty response.");
            var content = completion.Choices.FirstOrDefault()?.Message.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new AiModelProviderException("AI provider response did not contain generated content.");
            }

            return new(Key, settings.Model, content, completion.Id);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiModelProviderException("AI report generation timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new AiModelProviderException("AI provider endpoint could not be reached.", exception);
        }
        catch (JsonException exception)
        {
            throw new AiModelProviderException("AI provider returned an invalid response.", exception);
        }
    }

    protected virtual Uri GetApiBaseAddress(Uri configuredEndpoint)
    {
        var endpoint = configuredEndpoint.AbsoluteUri.TrimEnd('/');
        return endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? new Uri(endpoint + "/")
            : new Uri(endpoint + "/v1/");
    }

    private async Task<HttpClient> CreateClientAsync(AiProviderSettings settings, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(AiServiceCollectionExtensions.HttpClientName);
        client.BaseAddress = GetApiBaseAddress(settings.Endpoint);
        var credential = await credentials.GetCredentialAsync(settings, cancellationToken);
        if (!string.IsNullOrWhiteSpace(credential))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }

        return client;
    }

    private static CancellationTokenSource CreateTimeout(
        AiProviderSettings settings,
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(settings.Timeout);
        return source;
    }

    private sealed record ModelListResponse(IReadOnlyList<ModelDescription>? Data);
    private sealed record ModelDescription(string Id);
    private sealed record ChatMessage(string Role, string Content);
    private sealed record JsonSchemaDefinition(string Name, bool Strict, JsonElement Schema);
    private sealed record ResponseFormat(
        string Type,
        [property: JsonPropertyName("json_schema")] JsonSchemaDefinition JsonSchema);
    private sealed record ChatCompletionRequest(
        string Model,
        IReadOnlyList<ChatMessage> Messages,
        double Temperature,
        [property: JsonPropertyName("max_tokens")] int? MaximumOutputTokens,
        [property: JsonPropertyName("response_format")] ResponseFormat ResponseFormat);
    private sealed record ChatCompletionResponse(string? Id, IReadOnlyList<ChatChoice> Choices);
    private sealed record ChatChoice(ChatMessage Message);
}

public sealed class OllamaModelProvider(
    IHttpClientFactory httpClientFactory,
    IAiProviderCredentialAccessor credentials)
    : OpenAiCompatibleModelProvider(httpClientFactory, credentials)
{
    public override string Key => AiModelProviderKeys.Ollama;
}
