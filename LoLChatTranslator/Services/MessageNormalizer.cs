using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public sealed class MessageNormalizer
{
    private const string NormalizerPackRelativePath = "Resources/lol_chat_normalizer_pack_cn.json";

    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex DirectKeyTrailingPunctuationRegex = new(
        @"[\?？!！\.。,\uFF0C~～]+$",
        RegexOptions.Compiled);

    private readonly object _syncRoot = new();
    private readonly GlossaryMatcher _glossaryMatcher = new();
    private NormalizerPack? _pack;
    private List<OcrFixRule>? _ocrFixRules;

    public NormalizedMessage Normalize(
        string message,
        string toxicDisplayMode = "label",
        string targetLanguage = "zh-Hans")
    {
        var original = message;
        var normalized = OcrTextFixer.ApplyBuiltInFixes(NormalizeSpaces(message));
        var originalDirectKey = NormalizeDirectKey(normalized, trimTrailingPunctuation: true);

        var pack = LoadPack();

        if (pack is not null)
        {
            foreach (var rule in GetOcrFixRules(pack))
            {
                normalized = rule.CompiledRegex.Replace(normalized, rule.To);
            }
        }

        normalized = OcrTextFixer.ApplyBuiltInFixes(NormalizeSpaces(normalized));
        normalized = TranslationInputNormalizer.NormalizeForTranslation(normalized);
        var directTranslation = TryGetBuiltInDirectTranslation(originalDirectKey, normalized, targetLanguage);
        if (!string.IsNullOrWhiteSpace(directTranslation))
        {
            return new NormalizedMessage
            {
                Original = original,
                NormalizedText = NormalizeSpaces(normalized),
                DirectTranslation = directTranslation,
                IsTrustedDirectOutput = true,
                DirectOutputKind = "builtin_direct",
                GlossaryMatched = true,
                GlossaryMatchLevel = "builtin_direct",
                GlossaryMatchedEntry = originalDirectKey,
                GlossaryConfidence = 1
            };
        }

        var glossaryMatch = _glossaryMatcher.Match(normalized, toxicDisplayMode, targetLanguage);
        if (glossaryMatch.Matched && !string.IsNullOrWhiteSpace(glossaryMatch.OutputText))
        {
            return new NormalizedMessage
            {
                Original = original,
                NormalizedText = glossaryMatch.NormalizedText,
                DirectTranslation = glossaryMatch.OutputText,
                IsTrustedDirectOutput = true,
                DirectOutputKind = string.IsNullOrWhiteSpace(glossaryMatch.DirectOutputKind)
                    ? glossaryMatch.MatchLevel
                    : glossaryMatch.DirectOutputKind,
                GlossaryMatched = true,
                GlossaryMatchLevel = glossaryMatch.MatchLevel,
                GlossaryMatchedEntry = glossaryMatch.MatchedEntry,
                GlossaryConfidence = glossaryMatch.Confidence
            };
        }

        return new NormalizedMessage
        {
            Original = original,
            NormalizedText = NormalizeSpaces(normalized),
            GlossaryMatched = false,
            GlossaryMatchLevel = "none",
            GlossaryConfidence = 0
        };
    }

    private NormalizerPack? LoadPack()
    {
        lock (_syncRoot)
        {
            if (_pack is not null)
            {
                return _pack;
            }

            var path = Path.Combine(AppContext.BaseDirectory, NormalizerPackRelativePath);
            if (!File.Exists(path))
            {
                Trace.TraceError($"Message normalizer pack not found: {path}");
                return null;
            }

            try
            {
                var json = File.ReadAllText(path);
                _pack = JsonSerializer.Deserialize<NormalizerPack>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return _pack;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Trace.TraceError($"Failed to load message normalizer pack: {ex}");
                return null;
            }
        }
    }

    private List<OcrFixRule> GetOcrFixRules(NormalizerPack pack)
    {
        if (_ocrFixRules is not null)
        {
            return _ocrFixRules;
        }

        _ocrFixRules = pack.Rules?.OcrFixes?
            .Where(rule => !string.IsNullOrWhiteSpace(rule.FromRegex))
            .Select(rule =>
            {
                rule.CompiledRegex = new Regex(
                    rule.FromRegex,
                    RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return rule;
            })
            .ToList() ?? [];

        return _ocrFixRules;
    }

    private static string NormalizeDirectKey(string value, bool trimTrailingPunctuation)
    {
        var key = NormalizeSpaces(value.Normalize(NormalizationForm.FormKC)).ToLowerInvariant();
        if (trimTrailingPunctuation)
        {
            key = DirectKeyTrailingPunctuationRegex.Replace(key, string.Empty).Trim();
        }

        return key;
    }

    private static string? TryGetBuiltInDirectTranslation(
        string originalDirectKey,
        string normalized,
        string targetLanguage)
    {
        if (!TranslatorLanguage.IsAnyChinese(targetLanguage))
        {
            return null;
        }

        var normalizedKey = NormalizeDirectKey(normalized, trimTrailingPunctuation: true);
        foreach (var key in new[] { originalDirectKey, normalizedKey }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (OcrTextFixer.TryTranslateBuiltInPhrase(key, targetLanguage, out var translated))
            {
                return translated;
            }
        }

        return null;
    }

    private static string NormalizeSpaces(string value)
    {
        return OcrTextFixer.NormalizeReadableSpacing(MultiSpaceRegex.Replace(value.Trim(), " "));
    }

    private sealed class NormalizerPack
    {
        [JsonPropertyName("rules")]
        public NormalizerRules? Rules { get; set; }
    }

    private sealed class NormalizerRules
    {
        // Only ocr_fixes is consumed by the current pipeline; other JSON sections are retained for compatibility and review.
        [JsonPropertyName("ocr_fixes")]
        public List<OcrFixRule>? OcrFixes { get; set; }
    }

    private sealed class OcrFixRule
    {
        [JsonPropertyName("from_regex")]
        public string FromRegex { get; set; } = string.Empty;

        [JsonPropertyName("to")]
        public string To { get; set; } = string.Empty;

        [JsonIgnore]
        public Regex CompiledRegex { get; set; } = new("$^");
    }

}
