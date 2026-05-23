namespace LoLChatTranslator.Services;

public static class TranslatorLanguage
{
    public static readonly string[] SupportedTargetLanguages = ["zh-Hans", "zh-Hant", "en", "ko", "ja", "vi"];

    public const string DefaultTargetLanguage = "zh-Hans";

    public const string DefaultTargetLanguageDisplayName = "Simplified Chinese";

    public static bool IsSupportedTargetLanguage(string language)
    {
        return SupportedTargetLanguages.Contains(language, StringComparer.OrdinalIgnoreCase);
    }

    public static string NormalizeTargetLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return DefaultTargetLanguage;
        }

        if (language.Equals("zh", StringComparison.OrdinalIgnoreCase)
            || language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-Hans";
        }

        if (language.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)
            || language.Equals("zh-HK", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-Hant";
        }

        return IsSupportedTargetLanguage(language) ? language : DefaultTargetLanguage;
    }

    public static bool IsSimplifiedChinese(string language)
    {
        return language.Equals("zh", StringComparison.OrdinalIgnoreCase)
            || language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
            || language.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTraditionalChinese(string language)
    {
        return language.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)
            || language.Equals("zh-HK", StringComparison.OrdinalIgnoreCase)
            || language.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAnyChinese(string language)
    {
        return IsSimplifiedChinese(language) || IsTraditionalChinese(language);
    }

    public static string GetDisplayName(string language)
    {
        return language switch
        {
            "auto" => "自动检测",
            "zh" or "zh-CN" or "zh-Hans" => "简体中文",
            "zh-TW" or "zh-HK" or "zh-Hant" => "繁體中文",
            "en" => "English",
            "ko" => "한국어",
            "vi" => "Tiếng Việt",
            "ja" => "日本語",
            _ => string.IsNullOrWhiteSpace(language) ? "简体中文" : language
        };
    }

    public static string GetPromptTargetLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return DefaultTargetLanguageDisplayName;
        }

        return NormalizeTargetLanguage(language) switch
        {
            "zh-Hans" => DefaultTargetLanguageDisplayName,
            "zh-Hant" => "Traditional Chinese",
            "en" => "English",
            "ko" => "Korean",
            "ja" => "Japanese",
            "vi" => "Vietnamese",
            _ => DefaultTargetLanguageDisplayName
        };
    }

    public static string ToMyMemoryCode(string language)
    {
        if (IsTraditionalChinese(language))
        {
            return "zh-TW";
        }

        if (IsSimplifiedChinese(language))
        {
            return "zh-CN";
        }

        return language;
    }
}
