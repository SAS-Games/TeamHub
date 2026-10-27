using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeamHub.AI.Contracts;

namespace TeamHub.AI.ModelProviders;

public class OpenAiCompatibleModelProvider(
    IHttpClientFactory httpClientFactory,
    IAiProviderCredentialAccessor credentials) : IAiModelProvider, IAiModelCatalogProvider
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
            return new(false, Key, settings.Model, false, ConnectionFailureMessage(settings));
        }
        catch (JsonException)
        {
            return new(false, Key, settings.Model, false, "Provider returned an invalid model-list response.");
        }
    }

    public async Task<IReadOnlyList<AiModelCatalogItem>> GetModelsAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CreateTimeout(settings, cancellationToken);
            using var client = await CreateClientAsync(settings, timeout.Token);
            using var response = await client.GetAsync("models", timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw await CreateResponseExceptionAsync(
                    response,
                    "loading available models",
                    timeout.Token);

            var models = await response.Content.ReadFromJsonAsync<ModelListResponse>(SerializerOptions, timeout.Token);
            return models?.Data?
                .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .Select(model => new AiModelCatalogItem(
                    model.Id.Trim(), "chat", string.Empty, ["chat/completions"]))
                .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiModelProviderException("AI provider model discovery timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new AiModelProviderException(ConnectionFailureMessage(settings), exception);
        }
        catch (JsonException exception)
        {
            throw new AiModelProviderException("AI provider returned an invalid model-list response.", exception);
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
                    new("user", BuildChatUserContent(request))
                ],
                settings.Temperature,
                request.MaximumOutputTokens,
                new("json_schema", new("teamhub_structured_response", true, request.ResponseSchema)),
                string.Equals(Key, AiModelProviderKeys.LlamaCpp, StringComparison.OrdinalIgnoreCase)
                    ? new(false)
                    : null);

            using var response = await client.PostAsJsonAsync("chat/completions", payload, SerializerOptions, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateResponseExceptionAsync(
                    response,
                    "generating structured content",
                    timeout.Token);
            }

            var completion = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(SerializerOptions, timeout.Token)
                ?? throw new AiModelProviderException("AI provider returned an empty response.");
            var content = completion.Choices.FirstOrDefault()?.Message.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new AiModelProviderException("AI provider response did not contain generated content.");
            }

            var usage = completion.Usage is null
                ? null
                : new AiTokenUsage(
                    completion.Usage.PromptTokens,
                    completion.Usage.CompletionTokens,
                    completion.Usage.TotalTokens > 0
                        ? completion.Usage.TotalTokens
                        : completion.Usage.PromptTokens + completion.Usage.CompletionTokens);
            return new(Key, settings.Model, content, completion.Id, usage);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiModelProviderException("AI structured generation timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new AiModelProviderException(ConnectionFailureMessage(settings), exception);
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

    private string ConnectionFailureMessage(AiProviderSettings settings)
    {
        var endpoint = settings.Endpoint.AbsoluteUri.TrimEnd('/');
        return Key switch
        {
            AiModelProviderKeys.Ollama =>
                $"Ollama is not running or cannot be reached at {endpoint}. Start Ollama and try again.",
            AiModelProviderKeys.LlamaCpp =>
                $"llama.cpp is not running or cannot be reached at {endpoint}. Start the configured llama.cpp server and try again.",
            _ => $"The AI provider cannot be reached at {endpoint}. Check the endpoint in AI Services."
        };
    }

    private static async Task<AiModelProviderException> CreateResponseExceptionAsync(
        HttpResponseMessage response,
        string action,
        CancellationToken cancellationToken)
    {
        var detail = string.Empty;
        try
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(content))
            {
                using var document = JsonDocument.Parse(content);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var error))
                {
                    detail = error.ValueKind == JsonValueKind.String
                        ? error.GetString() ?? string.Empty
                        : error.TryGetProperty("message", out var message)
                            ? message.GetString() ?? string.Empty
                            : string.Empty;
                }
            }
        }
        catch (JsonException)
        {
            // Keep the stable HTTP error when a provider returns HTML or invalid JSON.
        }

        detail = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (detail.Length > 500) detail = detail[..500] + "…";
        var suffix = detail.Length == 0 ? string.Empty : $" {detail}";
        return new AiModelProviderException(
            $"AI provider returned HTTP {(int)response.StatusCode} while {action}.{suffix}");
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

    private static object BuildChatUserContent(AiStructuredGenerationRequest request)
    {
        if (request.Images is not { Count: > 0 }) return request.UserPrompt;
        var parts = new List<object> { new { type = "text", text = request.UserPrompt } };
        parts.AddRange(request.Images.Select(image => new
        {
            type = "image_url",
            image_url = new { url = $"data:{image.MediaType};base64,{image.Base64Data}" }
        }));
        return parts;
    }

    private sealed record ModelListResponse(IReadOnlyList<ModelDescription>? Data);
    private sealed record ModelDescription(string Id);
    private sealed record ChatRequestMessage(string Role, object Content);
    private sealed record ChatResponseMessage(string Role, string Content);
    private sealed record JsonSchemaDefinition(string Name, bool Strict, JsonElement Schema);
    private sealed record ResponseFormat(
        string Type,
        [property: JsonPropertyName("json_schema")] JsonSchemaDefinition JsonSchema);
    private sealed record ChatTemplateOptions(
        [property: JsonPropertyName("enable_thinking")] bool EnableThinking);
    private sealed record ChatCompletionRequest(
        string Model,
        IReadOnlyList<ChatRequestMessage> Messages,
        double Temperature,
        [property: JsonPropertyName("max_tokens")] int? MaximumOutputTokens,
        [property: JsonPropertyName("response_format")] ResponseFormat ResponseFormat,
        [property: JsonPropertyName("chat_template_kwargs")] ChatTemplateOptions? ChatTemplateKwargs);
    private sealed record ChatCompletionResponse(
        string? Id,
        IReadOnlyList<ChatChoice> Choices,
        ChatCompletionUsage? Usage);
    private sealed record ChatCompletionUsage(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
        [property: JsonPropertyName("total_tokens")] int TotalTokens);
    private sealed record ChatChoice(ChatResponseMessage Message);
}

public sealed class OllamaModelProvider(
    IHttpClientFactory httpClientFactory,
    IAiProviderCredentialAccessor credentials)
    : OpenAiCompatibleModelProvider(httpClientFactory, credentials)
{
    public override string Key => AiModelProviderKeys.Ollama;
}

public sealed class LlamaCppModelProvider(
    IHttpClientFactory httpClientFactory,
    IAiProviderCredentialAccessor credentials)
    : OpenAiCompatibleModelProvider(httpClientFactory, credentials)
{
    public override string Key => AiModelProviderKeys.LlamaCpp;
}
