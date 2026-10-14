using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TeamHub.AI.Contracts;

namespace TeamHub.AI.ModelProviders;

public sealed class PfbAiGatewayModelProvider(
    IHttpClientFactory httpClientFactory,
    IAiProviderCredentialAccessor credentials) : IAiModelProvider, IAiModelCatalogProvider
{
    public const string CommonChatMode = "CommonChat";
    public const string OpenAiChatMode = "OpenAiChat";
    public const string OpenAiResponsesMode = "OpenAiResponses";
    private const int MaximumAttempts = 4;

    public string Key => AiModelProviderKeys.PfbAiGateway;

    public async Task<AiProviderHealthResult> CheckHealthAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await GetModelsAsync(settings, cancellationToken);
            var available = models.Any(model =>
                string.Equals(model.Id, settings.Model, StringComparison.OrdinalIgnoreCase));
            return available
                ? new(true, Key, settings.Model, true, "PFB AI Gateway and configured model are available.")
                : new(true, Key, settings.Model, false,
                    $"PFB AI Gateway is available, but model '{settings.Model}' was not found in the portal catalog.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, Key, settings.Model, false, "PFB AI Gateway health check timed out.");
        }
        catch (HttpRequestException)
        {
            return new(false, Key, settings.Model, false, "PFB AI Gateway could not be reached.");
        }
        catch (JsonException)
        {
            return new(false, Key, settings.Model, false, "PFB AI Gateway returned an invalid model catalog.");
        }
        catch (InvalidOperationException exception)
        {
            return new(false, Key, settings.Model, false, exception.Message);
        }
        catch (AiModelProviderException exception)
        {
            return new(false, Key, settings.Model, false, exception.Message);
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
            using var response = await client.GetAsync("portal/pfb/models", timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new AiModelProviderException(
                    GatewayErrorMessage(response.StatusCode, await response.Content.ReadAsStringAsync(timeout.Token)));
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
            var models = new Dictionary<string, AiModelCatalogItem>(StringComparer.OrdinalIgnoreCase);
            CollectModels(document.RootElement, models);
            return models.Values.OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiModelProviderException("PFB AI Gateway model discovery timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new AiModelProviderException("PFB AI Gateway could not be reached.", exception);
        }
        catch (JsonException exception)
        {
            throw new AiModelProviderException("PFB AI Gateway returned an invalid model catalog.", exception);
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
            throw new ArgumentException("A JSON response schema is required.", nameof(request));

        try
        {
            using var timeout = CreateTimeout(settings, cancellationToken);
            using var client = await CreateClientAsync(settings, timeout.Token);
            var mode = NormalizeMode(settings.ApiMode);
            var payload = mode == OpenAiResponsesMode
                ? BuildResponsesPayload(settings, request)
                : BuildChatPayload(settings, request, includeSchema: mode == OpenAiChatMode);
            var path = mode switch
            {
                OpenAiChatMode => "pfb/openai/v1/chat/completions",
                OpenAiResponsesMode => "pfb/openai/v1/responses",
                _ => "pfb/common/v1/chat/completions"
            };

            var result = await PostWithRetryAsync(client, path, payload, timeout.Token);
            if (!result.IsSuccessStatusCode)
                throw new AiModelProviderException(GatewayErrorMessage(result.StatusCode, result.Body));

            using var document = JsonDocument.Parse(result.Body);
            var content = mode == OpenAiResponsesMode
                ? ReadResponsesContent(document.RootElement)
                : ReadChatContent(document.RootElement);
            if (string.IsNullOrWhiteSpace(content))
                throw new AiModelProviderException("PFB AI Gateway response did not contain generated content.");

            return new(Key, settings.Model, StripCodeFence(content), result.RequestId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiModelProviderException("PFB AI Gateway structured generation timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new AiModelProviderException("PFB AI Gateway could not be reached.", exception);
        }
        catch (JsonException exception)
        {
            throw new AiModelProviderException("PFB AI Gateway returned an invalid response.", exception);
        }
    }

    public async Task<AiProviderHealthResult> TestConnectionAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default)
    {
        var health = await CheckHealthAsync(settings, cancellationToken);
        if (!health.IsAvailable || !health.IsModelAvailable) return health;

        try
        {
            using var schema = JsonDocument.Parse(
                """{"type":"object","additionalProperties":false,"required":["status"],"properties":{"status":{"type":"string"}}}""");
            await GenerateStructuredAsync(
                settings,
                new AiStructuredGenerationRequest(
                    "Return only JSON matching the supplied schema.",
                    "Return a successful connection status.",
                    schema.RootElement.Clone(),
                    128),
                cancellationToken);
            return new(true, Key, settings.Model, true,
                $"PFB AI Gateway model and {NormalizeMode(settings.ApiMode)} route are available.");
        }
        catch (AiModelProviderException exception)
        {
            return new(false, Key, settings.Model, true, exception.Message);
        }
    }

    private async Task<HttpClient> CreateClientAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken)
    {
        var credential = await credentials.GetCredentialAsync(settings, cancellationToken);
        if (string.IsNullOrWhiteSpace(credential))
            throw new InvalidOperationException("A JWT bearer token is required for the PFB AI Gateway.");

        var client = httpClientFactory.CreateClient(AiServiceCollectionExtensions.HttpClientName);
        client.BaseAddress = new Uri(settings.Endpoint.AbsoluteUri.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.Trim());
        return client;
    }

    private static Dictionary<string, object?> BuildChatPayload(
        AiProviderSettings settings,
        AiStructuredGenerationRequest request,
        bool includeSchema)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = settings.Model,
            ["messages"] = new[]
            {
                new Dictionary<string, string> { ["role"] = "system", ["content"] = request.SystemPrompt },
                new Dictionary<string, string> { ["role"] = "user", ["content"] = request.UserPrompt }
            },
            ["temperature"] = settings.Temperature
        };
        if (request.MaximumOutputTokens.HasValue)
            payload["max_tokens"] = request.MaximumOutputTokens.Value;
        if (includeSchema)
        {
            payload["response_format"] = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "teamhub_structured_response",
                    strict = true,
                    schema = request.ResponseSchema
                }
            };
        }
        return payload;
    }

    private static Dictionary<string, object?> BuildResponsesPayload(
        AiProviderSettings settings,
        AiStructuredGenerationRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = settings.Model,
            ["input"] = $"{request.SystemPrompt}\n\nUser request:\n{request.UserPrompt}",
            ["temperature"] = settings.Temperature,
            ["text"] = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "teamhub_structured_response",
                    strict = true,
                    schema = request.ResponseSchema
                }
            }
        };
        if (request.MaximumOutputTokens.HasValue)
            payload["max_output_tokens"] = request.MaximumOutputTokens.Value;
        return payload;
    }

    private static async Task<GatewayResponse> PostWithRetryAsync(
        HttpClient client,
        string path,
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            using var response = await client.PostAsJsonAsync(path, payload, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.BadRequest
                && payload.Remove("temperature")
                && body.Contains("temperature", StringComparison.OrdinalIgnoreCase)
                && attempt < MaximumAttempts - 1)
            {
                continue;
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests
                or HttpStatusCode.BadGateway
                or HttpStatusCode.GatewayTimeout
                && attempt < MaximumAttempts - 1)
            {
                var delay = response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            var requestId = response.Headers.TryGetValues("x-request-id", out var values)
                ? values.FirstOrDefault()
                : null;
            return new(response.StatusCode, response.IsSuccessStatusCode, body, requestId);
        }

        throw new AiModelProviderException("PFB AI Gateway request failed after retrying.");
    }

    private static string ReadChatContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            return string.Empty;
        var message = choices[0].GetProperty("message");
        if (!message.TryGetProperty("content", out var content)) return string.Empty;
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array) return string.Empty;
        return string.Join("\n", content.EnumerateArray()
            .Where(item => item.TryGetProperty("text", out _))
            .Select(item => item.GetProperty("text").GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string ReadResponsesContent(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var outputText)
            && outputText.ValueKind == JsonValueKind.String)
            return outputText.GetString() ?? string.Empty;
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var parts = new List<string>();
        foreach (var block in output.EnumerateArray())
        {
            if (!block.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    parts.Add(text.GetString()!);
            }
        }
        return string.Join("\n", parts);
    }

    private static void CollectModels(
        JsonElement element,
        IDictionary<string, AiModelCatalogItem> models)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) CollectModels(item, models);
            return;
        }
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                models.TryAdd(value, new(value, string.Empty, string.Empty, []));
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        foreach (var name in new[] { "id", "model", "model_id", "name" })
        {
            if (element.TryGetProperty(name, out var id) && id.ValueKind == JsonValueKind.String)
            {
                var value = id.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    models[value] = new(
                        value,
                        ReadString(element, "type", "model_type"),
                        ReadString(element, "cost_tier", "costTier", "tier"),
                        ReadStringList(element, "endpoints", "endpoint"));
                }
                return;
            }
        }
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                CollectModels(property.Value, models);
        }
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String)
                return string.IsNullOrWhiteSpace(value.GetString()) ? [] : [value.GetString()!];
            if (value.ValueKind == JsonValueKind.Array)
            {
                return value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .ToList();
            }
        }
        return [];
    }

    private static string NormalizeMode(string? value) => value?.Trim() switch
    {
        OpenAiChatMode => OpenAiChatMode,
        OpenAiResponsesMode => OpenAiResponsesMode,
        _ => CommonChatMode
    };

    private static string GatewayErrorMessage(HttpStatusCode statusCode, string body)
    {
        var hint = statusCode switch
        {
            HttpStatusCode.Unauthorized => "Missing, expired, or invalid JWT bearer token.",
            HttpStatusCode.Forbidden => "The JWT is valid but does not permit this model or endpoint.",
            HttpStatusCode.TooManyRequests => "The gateway rate limit was exceeded.",
            HttpStatusCode.BadGateway => "The upstream AI provider returned an error.",
            HttpStatusCode.GatewayTimeout => "The upstream AI provider timed out.",
            _ => ReadErrorDetail(body)
        };
        return $"PFB AI Gateway returned HTTP {(int)statusCode}. {hint}";
    }

    private static string ReadErrorDetail(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString()!;
                if (error.TryGetProperty("message", out var message)) return message.GetString() ?? "Request failed.";
            }
            if (root.TryGetProperty("message", out var direct)) return direct.GetString() ?? "Request failed.";
        }
        catch (JsonException)
        {
        }
        return string.IsNullOrWhiteSpace(body) ? "Request failed." : body[..Math.Min(body.Length, 500)];
    }

    private static string StripCodeFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstLine = trimmed.IndexOf('\n');
        if (firstLine >= 0) trimmed = trimmed[(firstLine + 1)..];
        if (trimmed.EndsWith("```", StringComparison.Ordinal)) trimmed = trimmed[..^3];
        return trimmed.Trim();
    }

    private static CancellationTokenSource CreateTimeout(
        AiProviderSettings settings,
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(settings.Timeout);
        return source;
    }

    private sealed record GatewayResponse(
        HttpStatusCode StatusCode,
        bool IsSuccessStatusCode,
        string Body,
        string? RequestId);
}
