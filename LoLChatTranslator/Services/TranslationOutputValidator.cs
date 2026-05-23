namespace LoLChatTranslator.Services;

public static class TranslationOutputValidator
{
    public static bool TryBuildDisplayTranslation(
        string sourceText,
        string translatedText,
        string targetLanguage,
        bool allowTrustedDirectOutput,
        out string displayTranslation)
    {
        displayTranslation = translatedText.Trim();
        if (TranslatorErrorSanitizer.IsErrorResult(displayTranslation))
        {
            displayTranslation = string.Empty;
            return false;
        }

        if (allowTrustedDirectOutput)
        {
            return !string.IsNullOrWhiteSpace(displayTranslation);
        }

        if (string.IsNullOrWhiteSpace(displayTranslation)
            || OcrTextFixer.LooksUntranslated(sourceText, displayTranslation, targetLanguage))
        {
            displayTranslation = string.Empty;
            return false;
        }

        if (!TranslatorLanguage.IsAnyChinese(targetLanguage))
        {
            return true;
        }

        if (LooksLikeChinese(displayTranslation)
            && !OcrTextFixer.HasSuspiciousEnglishResidueForChineseTarget(displayTranslation))
        {
            return true;
        }

        displayTranslation = string.Empty;
        return false;
    }

    private static bool LooksLikeChinese(string value)
    {
        return value.Any(ch => ch is >= '\u4e00' and <= '\u9fff');
    }
}
