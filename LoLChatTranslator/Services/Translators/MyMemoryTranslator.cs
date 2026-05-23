using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace LoLChatTranslator.Services;

public sealed class MyMemoryTranslator : ITranslator
{
    private const string Endpoint = "https://api.mymemory.translated.net/get";

    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "zh-CN",
        "zh-TW",
        "en",
        "ko",
        "ja",
        "vi"
    };

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly Regex EnglishSegmentRegex = new(
        @"(?<![A-Za-z])(?:[A-Za-z][A-Za-z'’]*(?:\s+[A-Za-z][A-Za-z'’]*)*)(?![A-Za-z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EnglishTokenRegex = new(
        @"[A-Za-z]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> PreserveEnglishTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "adc", "ad", "ap", "aoe", "baron", "bot", "buff", "cd", "cs", "drake", "ff", "flash", "gank",
        "gg", "jg", "jgl", "jungle", "kda", "lol", "mid", "mia", "ss", "top", "tp", "ult", "ulti", "ward"
    };

    public async Task<string> TranslateAsync(
        string text,
        string targetLanguage,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default)
    {
        var target = TranslatorLanguage.ToMyMemoryCode(targetLanguage);
        if (ShouldTranslateMixedChineseAndEnglish(text, target, sourceLanguage))
        {
            return await TranslateMixedChineseAndEnglishAsync(text, target, cancellationToken);
        }

        var source = ToSourceLanguage(sourceLanguage, text);
        var languagePair = $"{source}|{target}";

        if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        if (!SupportedLanguages.Contains(source) || !SupportedLanguages.Contains(target))
        {
            LogError($"MyMemory unsupported language pair: {languagePair}");
            return $"[MyMemory 翻译失败] 不支持的语言组合：{languagePair}";
        }

        try
        {
            return await RequestTranslationAsync(text, languagePair, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            LogError("MyMemory request failed.", ex);
            return $"[MyMemory 翻译失败] {TranslatorErrorSanitizer.Sanitize(ex.Message)}";
        }
    }

    private static string ToSourceLanguage(string? language, string text)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return DetectSourceLanguage(text);
        }

        return TranslatorLanguage.ToMyMemoryCode(language);
    }

    private static string DetectSourceLanguage(string text)
    {
        var chineseCount = text.Count(ch => ch is >= '\u4e00' and <= '\u9fff');
        var asciiLetterCount = text.Count(char.IsAsciiLetter);
        if (chineseCount > 0
            && (asciiLetterCount == 0 || chineseCount >= Math.Max(2, asciiLetterCount / 2)))
        {
            return LooksLikeTraditionalChinese(text) ? "zh-TW" : "zh-CN";
        }

        if (text.Any(ch => ch is >= '\uac00' and <= '\ud7af'))
        {
            return "ko";
        }

        if (text.Any(ch => ch is >= '\u3040' and <= '\u30ff'))
        {
            return "ja";
        }

        const string vietnameseMarkers = "ăâđêôơưáàảãạấầẩẫậắằẳẵặéèẻẽẹếềểễệíìỉĩịóòỏõọốồổỗộớờởỡợúùủũụứừửữựýỳỷỹỵ";
        if (text.Any(ch => vietnameseMarkers.Contains(char.ToLowerInvariant(ch))))
        {
            return "vi";
        }

        return "en";
    }

    private static async Task<string> TranslateMixedChineseAndEnglishAsync(
        string text,
        string target,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var cursor = 0;
        foreach (Match match in EnglishSegmentRegex.Matches(text))
        {
            builder.Append(text, cursor, match.Index - cursor);
            var segment = match.Value;
            if (!ShouldTranslateEnglishSegment(segment))
            {
                builder.Append(segment);
            }
            else
            {
                var translatedSegment = await RequestTranslationAsync(
                    OcrTextFixer.ApplyBuiltInFixes(segment),
                    $"en|{target}",
                    cancellationToken);
                if (TranslatorErrorSanitizer.IsErrorResult(translatedSegment))
                {
                    return translatedSegment;
                }

                builder.Append(translatedSegment);
            }

            cursor = match.Index + match.Length;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return OcrTextFixer.NormalizeReadableSpacing(builder.ToString());
    }

    private static async Task<string> RequestTranslationAsync(
        string text,
        string languagePair,
        CancellationToken cancellationToken)
    {
        var url = $"{Endpoint}?q={Uri.EscapeDataString(text)}&langpair={Uri.EscapeDataString(languagePair)}";
        var payload = await HttpClient.GetFromJsonAsync<MyMemoryTranslateResponse>(url, cancellationToken);
        var translatedText = payload?.ResponseData?.TranslatedText;

        if (string.IsNullOrWhiteSpace(translatedText))
        {
            LogError("MyMemory response did not contain responseData.translatedText.");
            return "[MyMemory 翻译失败] 翻译服务返回为空。";
        }

        return WebUtility.HtmlDecode(translatedText);
    }

    private static bool ShouldTranslateMixedChineseAndEnglish(
        string text,
        string target,
        string? sourceLanguage)
    {
        if (!target.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
            && !target.Equals("zh-TW", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(sourceLanguage)
            && !sourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return text.Any(ch => ch is >= '\u4e00' and <= '\u9fff')
            && EnglishSegmentRegex.Matches(text).Any(match => ShouldTranslateEnglishSegment(match.Value));
    }

    private static bool ShouldTranslateEnglishSegment(string segment)
    {
        var tokens = EnglishTokenRegex.Matches(segment)
            .Select(match => match.Value)
            .ToList();
        if (tokens.Count == 0)
        {
            return false;
        }

        if (tokens.All(token => PreserveEnglishTerms.Contains(token)))
        {
            return false;
        }

        var letterCount = tokens.Sum(token => token.Length);
        if (tokens.Count == 1)
        {
            return tokens[0].Length >= 8 && !PreserveEnglishTerms.Contains(tokens[0]);
        }

        return letterCount >= 5;
    }

    private static bool LooksLikeTraditionalChinese(string text)
    {
        const string traditionalMarkers = "體繁簡語譯後對與無開關隊戰龍風國這個來時會說";
        return text.Any(traditionalMarkers.Contains);
    }

    private static void LogError(string message, Exception? exception = null)
    {
        var fullMessage = exception is null ? message : $"{message} {exception.GetType().Name}: {exception.Message}";
        Trace.TraceError(fullMessage);

        try
        {
            AppLogService.AppendText(
                "translate.log",
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {fullMessage}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break translation fallback behavior.
        }
    }

    private sealed class MyMemoryTranslateResponse
    {
        [JsonPropertyName("responseData")]
        public MyMemoryResponseData? ResponseData { get; set; }
    }

    private sealed class MyMemoryResponseData
    {
        [JsonPropertyName("translatedText")]
        public string? TranslatedText { get; set; }
    }
}
