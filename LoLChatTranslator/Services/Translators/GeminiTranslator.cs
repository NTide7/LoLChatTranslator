using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public sealed class GeminiTranslator : ITranslator
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly TranslateConfig _config;

    public GeminiTranslator(TranslateConfig config)
    {
        _config = config;
    }

    public async Task<string> TranslateAsync(
        string text,
        string targetLanguage,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = TranslatorCredentialStore.GetApiKey(_config);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "[Gemini 未配置] 请填写 API Key。";
        }

        var model = TranslatorEngines.ResolveModel(_config);
        if (string.IsNullOrWhiteSpace(model))
        {
            return "[Gemini 未配置] 请填写 Model。";
        }

        Uri endpoint;
        try
        {
            endpoint = BuildGenerateContentEndpoint(TranslatorEngines.ResolveApiBase(_config), model);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            return "[Gemini 配置错误] API Base 无效。";
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(TranslatorEngines.ResolveTimeoutSeconds(_config)));

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Content = JsonContent.Create(BuildRequest(text, targetLanguage, sourceLanguage));

        try
        {
            using var response = await HttpClient.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return await TranslatorHttpErrorHelper.BuildHttpErrorAsync(response, _config, "Gemini", timeoutCts.Token);
            }

            var payload = await response.Content.ReadFromJsonAsync<GeminiGenerateResponse>(timeoutCts.Token);
            var translatedText = payload?.Candidates?
                .FirstOrDefault()?
                .Content?
                .Parts?
                .Select(part => part.Text)
                .Where(partText => !string.IsNullOrWhiteSpace(partText))
                .Aggregate(string.Empty, (current, partText) => current + partText)
                .Trim();

            return string.IsNullOrWhiteSpace(translatedText)
                ? "[Gemini 翻译失败] 响应中没有 candidates[0].content.parts.text。"
                : translatedText;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"[Gemini 超时] 请求超过 {TranslatorEngines.ResolveTimeoutSeconds(_config)} 秒。";
        }
        catch (HttpRequestException ex)
        {
            return $"[Gemini 请求失败] {TranslatorErrorSanitizer.Sanitize(ex.Message, _config)}";
        }
        catch (System.Text.Json.JsonException)
        {
            return "[Gemini 翻译失败] 响应不是有效 JSON。";
        }
    }

    private static Uri BuildGenerateContentEndpoint(string apiBase, string model)
    {
        if (string.IsNullOrWhiteSpace(apiBase))
        {
            throw new ArgumentException("API Base is required.", nameof(apiBase));
        }

        var trimmedBase = apiBase.Trim().TrimEnd('/');
        var normalizedModel = model.Trim();
        if (normalizedModel.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            normalizedModel = normalizedModel["models/".Length..];
        }

        return new Uri($"{trimmedBase}/models/{Uri.EscapeDataString(normalizedModel)}:generateContent");
    }

    private static GeminiGenerateRequest BuildRequest(
        string text,
        string targetLanguage,
        string? sourceLanguage)
    {
        var prompt = TranslatorPromptBuilder.BuildSinglePrompt(text, targetLanguage, sourceLanguage);

        return new GeminiGenerateRequest(
            [new GeminiContent([new GeminiPart(prompt)])],
            new GeminiGenerationConfig(Temperature: 0.2));
    }

    private sealed record GeminiGenerateRequest(
        [property: JsonPropertyName("contents")] IReadOnlyList<GeminiContent> Contents,
        [property: JsonPropertyName("generationConfig")] GeminiGenerationConfig GenerationConfig);

    private sealed record GeminiGenerationConfig(
        [property: JsonPropertyName("temperature")] double Temperature);

    private sealed record GeminiContent(
        [property: JsonPropertyName("parts")] IReadOnlyList<GeminiPart> Parts);

    private sealed record GeminiPart(
        [property: JsonPropertyName("text")] string Text);

    private sealed class GeminiGenerateResponse
    {
        [JsonPropertyName("candidates")]
        public List<GeminiCandidate>? Candidates { get; set; }
    }

    private sealed class GeminiCandidate
    {
        [JsonPropertyName("content")]
        public GeminiResponseContent? Content { get; set; }
    }

    private sealed class GeminiResponseContent
    {
        [JsonPropertyName("parts")]
        public List<GeminiResponsePart>? Parts { get; set; }
    }

    private sealed class GeminiResponsePart
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
