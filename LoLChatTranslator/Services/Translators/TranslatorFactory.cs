using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public static class TranslatorFactory
{
    public static ITranslator Create(TranslateConfig config)
    {
        return TranslatorEngines.Normalize(config.TranslateEngine) switch
        {
            TranslatorEngines.AiApi when IsGeminiApiBase(config.ApiBase) => new GeminiTranslator(config),
            TranslatorEngines.AiApi => new OpenAICompatibleTranslator(config, "AI API"),
            TranslatorEngines.Ollama => new OpenAICompatibleTranslator(config, "Ollama"),
            _ => new MyMemoryTranslator()
        };
    }

    private static bool IsGeminiApiBase(string? apiBase)
    {
        return !string.IsNullOrWhiteSpace(apiBase)
            && apiBase.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase);
    }
}
