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

            var generated = ReadGeneratedContent(mode, result.Body);
            var usage = ReadTokenUsage(result.Body);
            var retriedEmptyContent = false;
            while ((generated.OutputLimitReached
                    || (!retriedEmptyContent && string.IsNullOrWhiteSpace(generated.Content)))
                && IncreaseOutputTokenLimit(payload, mode))
            {
                if (string.IsNullOrWhiteSpace(generated.Content)) retriedEmptyContent = true;
                result = await PostWithRetryAsync(client, path, payload, timeout.Token);
                if (!result.IsSuccessStatusCode)
                    throw new AiModelProviderException(GatewayErrorMessage(result.StatusCode, result.Body));
                generated = ReadGeneratedContent(mode, result.Body);
                usage = AddTokenUsage(usage, ReadTokenUsage(result.Body));
            }

            var content = generated.Content;
            if (generated.OutputLimitReached)
                throw new AiModelProviderException(
                    "PFB AI Gateway exhausted the output-token limit before completing the answer. Try again with a smaller date range or a larger chat output-token limit.");
            if (string.IsNullOrWhiteSpace(content))
                throw new AiModelProviderException("PFB AI Gateway response did not contain generated content.");

            return new(Key, settings.Model, StripCodeFence(content), result.RequestId, usage);
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
                new Dictionary<string, object?> { ["role"] = "system", ["content"] = request.SystemPrompt },
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = BuildChatUserContent(request) }
            },
            ["temperature"] = settings.Temperature
        };
        if (request.MaximumOutputTokens.HasValue)
            payload["max_tokens"] = request.MaximumOutputTokens.Value;
        if (UsesReasoningEffort(settings.Model))
            payload["reasoning_effort"] = "low";
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
            ["input"] = BuildResponsesInput(request),
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
        if (UsesReasoningEffort(settings.Model))
            payload["reasoning"] = new { effort = "low" };
        return payload;
    }

    private static object BuildChatUserContent(AiStructuredGenerationRequest request)
    {
        if (request.Images is not { Count: > 0 }) return request.UserPrompt;
        var parts = new List<object> { new { type = "text", text = request.UserPrompt } };
        parts.AddRange(request.Images.Select(image => new
        {
            type = "image_url",
            image_url = new { url = DataUrl(image) }
        }));
        return parts;
    }

    private static object BuildResponsesInput(AiStructuredGenerationRequest request)
    {
        if (request.Images is not { Count: > 0 })
            return $"{request.SystemPrompt}\n\nUser request:\n{request.UserPrompt}";

        var userContent = new List<object> { new { type = "input_text", text = request.UserPrompt } };
        userContent.AddRange(request.Images.Select(image => new
        {
            type = "input_image",
            image_url = DataUrl(image)
        }));
        return new object[]
        {
            new
            {
                role = "system",
                content = new object[] { new { type = "input_text", text = request.SystemPrompt } }
            },
            new { role = "user", content = userContent }
        };
    }

    private static string DataUrl(AiImageInput image) =>
        $"data:{image.MediaType};base64,{image.Base64Data}";

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
            if (response.StatusCode == HttpStatusCode.BadRequest && attempt < MaximumAttempts - 1)
            {
                if (body.Contains("temperature", StringComparison.OrdinalIgnoreCase)
                    && payload.Remove("temperature"))
                    continue;
                if (body.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase)
                    && payload.Remove("reasoning_effort"))
                    continue;
                if (body.Contains("reasoning", StringComparison.OrdinalIgnoreCase)
                    && payload.Remove("reasoning"))
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
        var direct = ReadFirstText(root, "output_text", "generated_text", "content");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            return ReadWrappedChatContent(root);
        if (choices[0].TryGetProperty("text", out var choiceText)
            && ReadText(choiceText) is { Length: > 0 } choiceValue)
            return choiceValue;
        if (!choices[0].TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object)
            return ReadWrappedChatContent(root);
        if (message.TryGetProperty("content", out var content))
        {
            var messageContent = ReadText(content);
            if (!string.IsNullOrWhiteSpace(messageContent)) return messageContent;
        }
        if (choices[0].TryGetProperty("delta", out var delta)
            && delta.ValueKind == JsonValueKind.Object
            && delta.TryGetProperty("content", out var deltaContent))
            return ReadText(deltaContent);
        return string.Empty;
    }

    private static string ReadWrappedChatContent(JsonElement root)
    {
        foreach (var propertyName in new[] { "data", "result", "response", "body" })
        {
            if (!root.TryGetProperty(propertyName, out var wrapper)) continue;
            if (wrapper.ValueKind == JsonValueKind.Object)
            {
                var value = ReadChatContent(wrapper);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            else if (wrapper.ValueKind == JsonValueKind.String)
            {
                var value = wrapper.GetString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (!value.TrimStart().StartsWith('{')) return value;
                try
                {
                    using var nested = JsonDocument.Parse(value);
                    var nestedContent = ReadChatContent(nested.RootElement);
                    if (!string.IsNullOrWhiteSpace(nestedContent)) return nestedContent;
                }
                catch (JsonException)
                {
                    // A non-JSON wrapper string is not generated chat content.
                }
            }
        }
        return string.Empty;
    }

    private static string ReadFirstText(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value)) continue;
            var text = ReadText(value);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return string.Empty;
    }

    private static string ReadText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString() ?? string.Empty;
        if (element.ValueKind == JsonValueKind.Object)
            return ReadFirstText(element, "text", "value", "output_text", "content");
        if (element.ValueKind != JsonValueKind.Array) return string.Empty;

        return string.Join("\n", element.EnumerateArray()
            .Select(ReadText)
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

    private static GeneratedContent ReadGeneratedContent(string mode, string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var content = mode == OpenAiResponsesMode
            ? ReadResponsesContent(root)
            : ReadChatContent(root);
        return new(content, OutputLimitReached(root, mode));
    }

    private static bool OutputLimitReached(JsonElement root, string mode)
    {
        if (mode == OpenAiResponsesMode)
        {
            return root.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), "incomplete", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("incomplete_details", out var details)
                && details.ValueKind == JsonValueKind.Object
                && details.TryGetProperty("reason", out var reason)
                && string.Equals(reason.GetString(), "max_output_tokens", StringComparison.OrdinalIgnoreCase);
        }

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("finish_reason", out var finishReason)
            || finishReason.ValueKind != JsonValueKind.String)
            return false;

        return finishReason.GetString() is string value
            && (value.Equals("length", StringComparison.OrdinalIgnoreCase)
                || value.Equals("max_tokens", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IncreaseOutputTokenLimit(
        IDictionary<string, object?> payload,
        string mode)
    {
        var property = mode == OpenAiResponsesMode ? "max_output_tokens" : "max_tokens";
        if (!payload.TryGetValue(property, out var configured) || configured is not int current)
            return false;

        const int maximumRetryTokens = 32_000;
        if (current >= maximumRetryTokens) return false;
        payload[property] = Math.Min(maximumRetryTokens, Math.Max(current * 2, 4_000));
        return true;
    }

    private static AiTokenUsage? ReadTokenUsage(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var input = ReadUsageValue(usage, "input_tokens", "prompt_tokens", "inputTokens", "promptTokens");
        var output = ReadUsageValue(usage, "output_tokens", "completion_tokens", "outputTokens", "completionTokens");
        var total = ReadUsageValue(usage, "total_tokens", "totalTokens");
        if (input == 0 && output == 0 && total == 0) return null;
        if (total == 0) total = input + output;
        return new AiTokenUsage(input, output, total);
    }

    private static int ReadUsageValue(JsonElement usage, params string[] names)
    {
        foreach (var name in names)
        {
            if (usage.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var count)
                && count >= 0)
                return count;
        }
        return 0;
    }

    private static AiTokenUsage? AddTokenUsage(AiTokenUsage? current, AiTokenUsage? next)
    {
        if (current is null) return next;
        if (next is null) return current;
        return new AiTokenUsage(
            checked(current.InputTokens + next.InputTokens),
            checked(current.OutputTokens + next.OutputTokens),
            checked(current.TotalTokens + next.TotalTokens));
    }

    private static bool UsesReasoningEffort(string model) =>
        model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase);

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

    private sealed record GeneratedContent(string Content, bool OutputLimitReached);
}
