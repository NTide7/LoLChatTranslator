using System.Text.RegularExpressions;

namespace LoLChatTranslator.Services;

public sealed record ChatIntentMatch(string ConceptId, string OutputText);

public static partial class ChatIntentMatcher
{
    private static readonly Dictionary<string, Dictionary<string, string>> Outputs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["request_gank_mid"] = new(StringComparer.OrdinalIgnoreCase)
        {
            [ChatLanguageDetector.ChineseSimplified] = "请来中路抓一下",
            [ChatLanguageDetector.ChineseTraditional] = "請來中路抓一下",
            [ChatLanguageDetector.English] = "pls gank mid",
            [ChatLanguageDetector.Korean] = "미드 갱좀",
            [ChatLanguageDetector.Japanese] = "ミッドガンクお願い",
            [ChatLanguageDetector.Vietnamese] = "gank mid đi"
        },
        ["request_gank_top"] = new(StringComparer.OrdinalIgnoreCase)
        {
            [ChatLanguageDetector.ChineseSimplified] = "来抓上",
            [ChatLanguageDetector.ChineseTraditional] = "來抓上",
            [ChatLanguageDetector.English] = "pls gank top",
            [ChatLanguageDetector.Korean] = "탑 갱좀",
            [ChatLanguageDetector.Japanese] = "トップガンクお願い",
            [ChatLanguageDetector.Vietnamese] = "gank top đi"
        },
        ["request_gank_bot"] = new(StringComparer.OrdinalIgnoreCase)
        {
            [ChatLanguageDetector.ChineseSimplified] = "来抓下",
            [ChatLanguageDetector.ChineseTraditional] = "來抓下",
            [ChatLanguageDetector.English] = "pls gank bot",
            [ChatLanguageDetector.Korean] = "봇 갱좀",
            [ChatLanguageDetector.Japanese] = "ボットガンクお願い",
            [ChatLanguageDetector.Vietnamese] = "gank bot đi"
        }
    };

    public static ChatIntentMatch? Match(string input, string targetLang)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var normalized = Normalize(input);
        var conceptId = ResolveConceptId(normalized);
        if (conceptId is null)
        {
            return null;
        }

        var normalizedTarget = NormalizeSourceLangCode(targetLang);
        return Outputs.TryGetValue(conceptId, out var outputs)
            && outputs.TryGetValue(normalizedTarget, out var output)
                ? new ChatIntentMatch(conceptId, output)
                : null;
    }

    private static string? ResolveConceptId(string normalized)
    {
        if (IsGankRequest(normalized)
            && (normalized.Contains("中", StringComparison.Ordinal)
                || normalized.Contains("mid", StringComparison.Ordinal)
                || normalized.Contains("middle", StringComparison.Ordinal)
                || normalized.Contains("미드", StringComparison.Ordinal)
                || normalized.Contains("ミッド", StringComparison.Ordinal)))
        {
            return "request_gank_mid";
        }

        if (IsGankRequest(normalized)
            && (normalized.Contains("上", StringComparison.Ordinal)
                || normalized.Contains("top", StringComparison.Ordinal)
                || normalized.Contains("탑", StringComparison.Ordinal)
                || normalized.Contains("トップ", StringComparison.Ordinal)))
        {
            return "request_gank_top";
        }

        if (IsGankRequest(normalized)
            && (normalized.Contains("下", StringComparison.Ordinal)
                || normalized.Contains("bot", StringComparison.Ordinal)
                || normalized.Contains("bottom", StringComparison.Ordinal)
                || normalized.Contains("봇", StringComparison.Ordinal)
                || normalized.Contains("ボット", StringComparison.Ordinal)))
        {
            return "request_gank_bot";
        }

        return null;
    }

    private static bool IsGankRequest(string normalized)
    {
        return normalized.Contains("抓", StringComparison.Ordinal)
            || normalized.Contains("gank", StringComparison.Ordinal)
            || normalized.Contains("帮", StringComparison.Ordinal)
            || normalized.Contains("幫", StringComparison.Ordinal)
            || normalized.Contains("打野来", StringComparison.Ordinal)
            || normalized.Contains("打野來", StringComparison.Ordinal)
            || normalized.Contains("갱", StringComparison.Ordinal)
            || normalized.Contains("ガンク", StringComparison.Ordinal);
    }

    private static string NormalizeSourceLangCode(string targetLang)
    {
        if (targetLang.Contains('_', StringComparison.Ordinal))
        {
            return targetLang;
        }

        return ChatLanguageDetector.ToSourceLangCode(targetLang);
    }

    private static string Normalize(string input)
    {
        var text = WhitespaceRegex().Replace(input.Trim().ToLowerInvariant(), string.Empty);
        return PunctuationRegex().Replace(text, string.Empty);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[，。！？!?.,;；:：、\[\]【】（）()""'`~\-_/\\]+")]
    private static partial Regex PunctuationRegex();
}
