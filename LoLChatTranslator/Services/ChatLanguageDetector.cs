namespace LoLChatTranslator.Services;

public sealed record ChatLanguageDetection(string SourceLang, double Confidence);

public static class ChatLanguageDetector
{
    public const string AutoReverse = "ocr-reverse";
    public const string ChineseSimplified = "zh_CN";
    public const string ChineseTraditional = "zh_TW";
    public const string English = "en_US";
    public const string Korean = "ko_KR";
    public const string Japanese = "ja_JP";
    public const string Vietnamese = "vi_VN";

    private static readonly char[] TraditionalChineseHints =
    [
        '來', '幫', '團', '龍', '買', '賣', '開', '關', '點', '擊', '護', '藍', '紅', '閃', '傳'
    ];

    private static readonly string[] VietnameseHints =
    [
        "đ", "ă", "â", "ê", "ô", "ơ", "ư", " rồng", " đi", " đẩy", " giúp", " tôi"
    ];

    private static readonly string[] EnglishHints =
    [
        "pls", "plz", "please", "gank", "mid", "top", "bot", "jg", "jungle", "push", "baron", "drake",
        "dragon", "ult", "flash", "tp", "ward", "roam", "miss", "ss", "help"
    ];

    public static ChatLanguageDetection Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ChatLanguageDetection(English, 0);
        }

        var normalized = text.Trim().ToLowerInvariant();
        if (normalized.Any(ch => ch is >= '\u3040' and <= '\u30ff'))
        {
            return new ChatLanguageDetection(Japanese, 0.95);
        }

        if (normalized.Any(ch => ch is >= '\uac00' and <= '\ud7af'))
        {
            return new ChatLanguageDetection(Korean, 0.95);
        }

        if (normalized.Any(ch => ch is >= '\u4e00' and <= '\u9fff'))
        {
            var language = normalized.Any(ch => TraditionalChineseHints.Contains(ch))
                ? ChineseTraditional
                : ChineseSimplified;
            return new ChatLanguageDetection(language, 0.9);
        }

        if (VietnameseHints.Any(normalized.Contains))
        {
            return new ChatLanguageDetection(Vietnamese, 0.82);
        }

        if (EnglishHints.Any(normalized.Contains) || normalized.All(ch => ch < 128))
        {
            return new ChatLanguageDetection(English, 0.86);
        }

        return new ChatLanguageDetection(English, 0.45);
    }

    public static string ToTranslatorLanguage(string sourceLang)
    {
        return sourceLang switch
        {
            ChineseSimplified => "zh-Hans",
            ChineseTraditional => "zh-Hant",
            English => "en",
            Korean => "ko",
            Japanese => "ja",
            Vietnamese => "vi",
            _ => "en"
        };
    }

    public static string ToSourceLangCode(string translatorLanguage)
    {
        return TranslatorLanguage.NormalizeTargetLanguage(translatorLanguage) switch
        {
            "zh-Hans" => ChineseSimplified,
            "zh-Hant" => ChineseTraditional,
            "en" => English,
            "ko" => Korean,
            "ja" => Japanese,
            "vi" => Vietnamese,
            _ => English
        };
    }
}
