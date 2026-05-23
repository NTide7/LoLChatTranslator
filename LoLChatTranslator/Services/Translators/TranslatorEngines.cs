using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public static class TranslatorEngines
{
    public const string MyMemoryFree = "MyMemory免费翻译";
    public const string AiApi = "AiApi";
    public const string Ollama = "Ollama";
    public const string OpenAICompatible = "OpenAICompatible";
    public const string DeepSeekPreset = "DeepSeekPreset";
    public const string Gemini = "Gemini";

    public const string OpenAICompatibleDefaultApiBase = "https://api.openai.com/v1";
    public const string OpenAICompatibleDefaultModel = "gpt-4.1-mini";
    public const string DeepSeekDefaultApiBase = "https://api.deepseek.com/v1";
    public const string DeepSeekDefaultModel = "deepseek-chat";
    public const string GeminiDefaultApiBase = "https://generativelanguage.googleapis.com/v1beta";
    public const string GeminiDefaultModel = "gemini-2.5-flash";
    public const string OllamaDefaultApiBase = "http://localhost:11434/v1";
    public const string OllamaDefaultModel = "llama3.1";

    public static string Normalize(string? engine)
    {
        if (string.IsNullOrWhiteSpace(engine))
        {
            return MyMemoryFree;
        }

        var value = engine.Trim();
        if (value.Equals("Default", StringComparison.OrdinalIgnoreCase)
            || value.Equals("MyMemory", StringComparison.OrdinalIgnoreCase)
            || value.Equals("MyMemoryTranslate", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Free", StringComparison.OrdinalIgnoreCase))
        {
            return MyMemoryFree;
        }

        if (value.Equals("AI API", StringComparison.OrdinalIgnoreCase)
            || value.Equals("AiApi", StringComparison.OrdinalIgnoreCase)
            || value.Equals(OpenAICompatible, StringComparison.OrdinalIgnoreCase)
            || value.Equals("OpenAI Compatible", StringComparison.OrdinalIgnoreCase)
            || value.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
            || value.Equals(DeepSeekPreset, StringComparison.OrdinalIgnoreCase)
            || value.Equals("DeepSeek", StringComparison.OrdinalIgnoreCase)
            || value.Equals("DeepSeek Preset", StringComparison.OrdinalIgnoreCase)
            || value.Equals(Gemini, StringComparison.OrdinalIgnoreCase))
        {
            return AiApi;
        }

        if (value.Equals(Ollama, StringComparison.OrdinalIgnoreCase)
            || value.Equals("OllamaLocal", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Ollama 本地模型", StringComparison.OrdinalIgnoreCase))
        {
            return Ollama;
        }

        return value;
    }

    public static bool UsesApiSettings(string? engine)
    {
        var normalized = Normalize(engine);
        return normalized.Equals(AiApi, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(Ollama, StringComparison.OrdinalIgnoreCase);
    }

    public static bool RequiresApiKey(string? engine)
    {
        return Normalize(engine).Equals(AiApi, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMyMemoryFreeTranslator(string? engine)
    {
        return Normalize(engine).Equals(MyMemoryFree, StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveApiBase(TranslateConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ApiBase))
        {
            return config.ApiBase.Trim();
        }

        return Normalize(config.TranslateEngine) switch
        {
            AiApi => OpenAICompatibleDefaultApiBase,
            Ollama => OllamaDefaultApiBase,
            _ => string.Empty
        };
    }

    public static string ResolveModel(TranslateConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Model))
        {
            return config.Model.Trim();
        }

        return Normalize(config.TranslateEngine) switch
        {
            AiApi => OpenAICompatibleDefaultModel,
            Ollama => OllamaDefaultModel,
            _ => string.Empty
        };
    }

    public static int ResolveTimeoutSeconds(TranslateConfig config)
    {
        return Math.Clamp(config.TimeoutSeconds <= 0 ? 20 : config.TimeoutSeconds, 5, 120);
    }
}
