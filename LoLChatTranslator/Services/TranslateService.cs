using System.Collections.Concurrent;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public sealed class TranslateService
{
    private const int MaxCacheEntries = 256;

    private readonly ConcurrentDictionary<TranslationCacheKey, string> _cache = new();
    private readonly ConcurrentQueue<TranslationCacheKey> _cacheOrder = new();
    private AppConfig _config;

    public TranslateService(AppConfig config)
    {
        _config = config;
    }

    public void UpdateConfig(AppConfig config)
    {
        _config = config;
    }

    public Task<string> TranslateAsync(string text)
    {
        return TranslateAsync(
            text,
            _config.TranslateConfig.TargetLanguage,
            _config.TranslateConfig.SourceLanguage);
    }

    public async Task<string> TranslateAsync(
        string text,
        string targetLanguage,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var translateConfig = _config.TranslateConfig;
        var effectiveSourceLanguage = string.IsNullOrWhiteSpace(sourceLanguage)
            ? translateConfig.SourceLanguage
            : sourceLanguage;
        var effectiveTargetLanguage = TranslatorLanguage.NormalizeTargetLanguage(
            string.IsNullOrWhiteSpace(targetLanguage)
                ? translateConfig.TargetLanguage
                : targetLanguage);
        if (!string.IsNullOrWhiteSpace(effectiveSourceLanguage)
            && !effectiveSourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && TranslatorLanguage.NormalizeTargetLanguage(effectiveSourceLanguage).Equals(effectiveTargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            effectiveSourceLanguage = "auto";
        }

        var providerName = TranslatorEngines.Normalize(translateConfig.TranslateEngine);
        var fixedText = OcrEnglishGlueFixer.FixMessageBody(text);
        var normalizedText = TranslationInputNormalizer.NormalizeForTranslation(fixedText);
        var cacheKey = TranslationCacheKey.Create(
            normalizedText,
            effectiveSourceLanguage,
            effectiveTargetLanguage,
            translateConfig);

        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            WriteTranslateInputLog(providerName, text, fixedText, normalizedText, normalizedText, cacheHit: true);
            return cached;
        }

        WriteTranslateInputLog(providerName, text, fixedText, normalizedText, normalizedText, cacheHit: false);

        try
        {
            if (OcrTextFixer.TryTranslateBuiltInPhrase(normalizedText, effectiveTargetLanguage, out var localTranslation))
            {
                WriteTranslateFallbackLog(providerName, normalizedText, effectiveTargetLanguage, "builtin_before_request", localTranslation);
                AddToCache(cacheKey, localTranslation);
                return localTranslation;
            }

            var translator = TranslatorFactory.Create(translateConfig);
            var translatedText = await translator.TranslateAsync(
                normalizedText,
                effectiveTargetLanguage,
                effectiveSourceLanguage,
                cancellationToken);
            translatedText = TranslationInputNormalizer.PostProcessTranslation(
                normalizedText,
                translatedText,
                effectiveTargetLanguage);

            if (IsUsableTranslation(normalizedText, translatedText, effectiveTargetLanguage))
            {
                AddToCache(cacheKey, translatedText);
                return translatedText;
            }

            var recoveredText = await TryRecoverSuspiciousTranslationAsync(
                translator,
                providerName,
                normalizedText,
                translatedText,
                effectiveSourceLanguage,
                effectiveTargetLanguage,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(recoveredText))
            {
                AddToCache(cacheKey, recoveredText);
                return recoveredText;
            }

            WriteTranslateFallbackLog(providerName, normalizedText, effectiveTargetLanguage, "unrecoverable_untranslated", translatedText);
            return TranslatorErrorSanitizer.IsErrorResult(translatedText)
                ? translatedText
                : "[翻译失败] 翻译结果疑似未翻译或半翻译，已跳过显示。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"[翻译失败] {TranslatorErrorSanitizer.Sanitize(ex.Message, translateConfig)}";
        }
    }

    private static bool IsUsableTranslation(string sourceText, string translatedText, string targetLanguage)
    {
        return !string.IsNullOrWhiteSpace(translatedText)
            && !TranslatorErrorSanitizer.IsErrorResult(translatedText)
            && !OcrTextFixer.LooksUntranslated(sourceText, translatedText, targetLanguage);
    }

    private static async Task<string?> TryRecoverSuspiciousTranslationAsync(
        ITranslator translator,
        string providerName,
        string normalizedText,
        string translatedText,
        string effectiveSourceLanguage,
        string effectiveTargetLanguage,
        CancellationToken cancellationToken)
    {
        if (OcrTextFixer.TryTranslateBuiltInPhrase(normalizedText, effectiveTargetLanguage, out var localTranslation))
        {
            WriteTranslateFallbackLog(providerName, normalizedText, effectiveTargetLanguage, "builtin_after_suspicious_result", localTranslation);
            return localTranslation;
        }

        var retryText = TranslationInputNormalizer.NormalizeForTranslation(OcrTextFixer.ApplyBuiltInFixes(normalizedText));
        if (!retryText.Equals(normalizedText, StringComparison.Ordinal))
        {
            WriteTranslateFallbackLog(providerName, normalizedText, effectiveTargetLanguage, "retry_normalized_input", retryText);
            var retryTranslation = await translator.TranslateAsync(
                retryText,
                effectiveTargetLanguage,
                effectiveSourceLanguage,
                cancellationToken);
            retryTranslation = TranslationInputNormalizer.PostProcessTranslation(
                retryText,
                retryTranslation,
                effectiveTargetLanguage);
            if (IsUsableTranslation(retryText, retryTranslation, effectiveTargetLanguage))
            {
                return retryTranslation;
            }

            if (OcrTextFixer.TryTranslateBuiltInPhrase(retryText, effectiveTargetLanguage, out localTranslation))
            {
                WriteTranslateFallbackLog(providerName, retryText, effectiveTargetLanguage, "builtin_after_retry", localTranslation);
                return localTranslation;
            }
        }

        if (TranslatorLanguage.IsAnyChinese(effectiveTargetLanguage)
            && normalizedText.Any(char.IsAsciiLetter))
        {
            WriteTranslateFallbackLog(providerName, normalizedText, effectiveTargetLanguage, "retry_force_en_source", translatedText);
            var retryTranslation = await translator.TranslateAsync(
                retryText,
                effectiveTargetLanguage,
                "en",
                cancellationToken);
            retryTranslation = TranslationInputNormalizer.PostProcessTranslation(
                retryText,
                retryTranslation,
                effectiveTargetLanguage);
            if (IsUsableTranslation(retryText, retryTranslation, effectiveTargetLanguage))
            {
                return retryTranslation;
            }
        }

        return null;
    }

    private static void WriteTranslateInputLog(
        string providerName,
        string rawText,
        string fixedText,
        string normalizedText,
        string cacheKeyText,
        bool cacheHit)
    {
        try
        {
            AppLogService.AppendVerboseText(
                "translate-input-debug.log",
                $"""
                {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [TranslateInput][Provider] {CleanLog(providerName)}
                {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [TranslateInput][Raw] {CleanLog(rawText)}
                {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [TranslateInput][Fixed] {CleanLog(fixedText)}
                {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [TranslateInput][Normalized] {CleanLog(normalizedText)}
                {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [TranslateInput][BeforeNormalize] {CleanLog(rawText)}
                {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [TranslateInput][AfterNormalize] {CleanLog(normalizedText)}
                {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [TranslateInput][CacheKeyText] {CleanLog(cacheKeyText)}
                {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [TranslateInput][CacheHit] {cacheHit.ToString().ToLowerInvariant()}

                """);
        }
        catch
        {
            // Translation input diagnostics must never interrupt translation.
        }
    }

    private static void WriteTranslateFallbackLog(
        string providerName,
        string sourceText,
        string targetLanguage,
        string reason,
        string detail)
    {
        try
        {
            AppLogService.AppendVerboseText(
                "translate-fallback-debug.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} provider={CleanLog(providerName)} target={CleanLog(targetLanguage)} reason={CleanLog(reason)} source=\"{CleanLog(sourceText)}\" detail=\"{CleanLog(detail)}\"{Environment.NewLine}");
        }
        catch
        {
            // Translation fallback diagnostics must never interrupt translation.
        }
    }

    private static string CleanLog(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value)
            ? "<none>"
            : value.ReplaceLineEndings(" ").Trim();
        return text.Length <= 500 ? text : $"{text[..500]}...";
    }

    private void AddToCache(TranslationCacheKey key, string value)
    {
        if (!_cache.TryAdd(key, value))
        {
            _cache[key] = value;
            return;
        }

        _cacheOrder.Enqueue(key);
        while (_cache.Count > MaxCacheEntries && _cacheOrder.TryDequeue(out var oldestKey))
        {
            _cache.TryRemove(oldestKey, out _);
        }
    }

    private sealed record TranslationCacheKey(
        string Engine,
        string ApiBase,
        string Model,
        string SourceLanguage,
        string TargetLanguage,
        string NormalizerVersion,
        string Text)
    {
        public static TranslationCacheKey Create(
            string text,
            string sourceLanguage,
            string targetLanguage,
            TranslateConfig config)
        {
            var engine = TranslatorEngines.Normalize(config.TranslateEngine);
            return new TranslationCacheKey(
                engine,
                TranslatorEngines.ResolveApiBase(config).ToLowerInvariant(),
                TranslatorEngines.ResolveModel(config).ToLowerInvariant(),
                sourceLanguage.Trim().ToLowerInvariant(),
                targetLanguage.Trim().ToLowerInvariant(),
                TranslationInputNormalizer.Version,
                text.Trim());
        }
    }
}
