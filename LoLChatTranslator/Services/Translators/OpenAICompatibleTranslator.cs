using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public sealed class OpenAICompatibleTranslator : ITranslator
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly TranslateConfig _config;
    private readonly string _serviceName;

    public OpenAICompatibleTranslator(TranslateConfig config, string serviceName)
    {
        _config = config;
        _serviceName = serviceName;
    }

    public async Task<string> TranslateAsync(
        string text,
        string targetLanguage,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = TranslatorCredentialStore.GetApiKey(_config);
        if (TranslatorEngines.RequiresApiKey(_config.TranslateEngine) && string.IsNullOrWhiteSpace(apiKey))
        {
            return $"[{_serviceName} 未配置] 请填写 API Key。";
        }

        var model = TranslatorEngines.ResolveModel(_config);
        if (string.IsNullOrWhiteSpace(model))
        {
            return $"[{_serviceName} 未配置] 请填写 Model。";
        }

        Uri endpoint;
        try
        {
            endpoint = BuildChatCompletionsEndpoint(TranslatorEngines.ResolveApiBase(_config));
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            return $"[{_serviceName} 配置错误] API Base 无效。";
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(TranslatorEngines.ResolveTimeoutSeconds(_config)));

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        request.Content = JsonContent.Create(BuildRequest(text, targetLanguage, sourceLanguage, model));

        try
        {
            using var response = await HttpClient.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return await TranslatorHttpErrorHelper.BuildHttpErrorAsync(response, _config, _serviceName, timeoutCts.Token);
            }

            var payload = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(timeoutCts.Token);
            var translatedText = payload?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();

            return string.IsNullOrWhiteSpace(translatedText)
                ? $"[{_serviceName} 翻译失败] 响应中没有 choices[0].message.content。"
                : translatedText;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"[{_serviceName} 超时] 请求超过 {TranslatorEngines.ResolveTimeoutSeconds(_config)} 秒。";
        }
        catch (HttpRequestException ex)
        {
            return $"[{_serviceName} 请求失败] {TranslatorErrorSanitizer.Sanitize(ex.Message, _config)}";
        }
        catch (System.Text.Json.JsonException)
        {
            return $"[{_serviceName} 翻译失败] 响应不是有效 JSON。";
        }
    }

    private static Uri BuildChatCompletionsEndpoint(string apiBase)
    {
        if (string.IsNullOrWhiteSpace(apiBase))
        {
            throw new ArgumentException("API Base is required.", nameof(apiBase));
        }

        var trimmed = apiBase.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(trimmed);
        }

        return new Uri($"{trimmed}/chat/completions");
    }

    private ChatCompletionRequest BuildRequest(
        string text,
        string targetLanguage,
        string? sourceLanguage,
        string model)
    {
        var messages = new[]
        {
            new ChatMessage(
                "system",
                TranslatorPromptBuilder.BuildSystemPrompt(targetLanguage)),
            new ChatMessage(
                "user",
                TranslatorPromptBuilder.BuildUserPrompt(text, targetLanguage, sourceLanguage))
        };

        return new ChatCompletionRequest(model, messages, Temperature: 0.2, Stream: false);
    }

    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<ChatChoice>? Choices { get; set; }
    }

    private sealed class ChatChoice
    {
        [JsonPropertyName("message")]
        public ChatChoiceMessage? Message { get; set; }
    }

    private sealed class ChatChoiceMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
