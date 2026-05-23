namespace LoLChatTranslator.Services;

public static class OcrEngines
{
    public const string WindowsOcr = "WindowsOCR";
    public const string PpOcrV5Multilingual = "PPOCRv5Multilingual";

    private static readonly HashSet<string> LegacyNonWindowsEngines = new(StringComparer.OrdinalIgnoreCase)
    {
        "RapidOCR",
        "PaddleOCR",
        "LocalScript",
        "Tesseract",
        "LocalPython",
        "EasyOCR",
        "Mock",
        "MockOCR",
        "Other"
    };

    public static string Normalize(string? value, out string? migratedFrom)
    {
        migratedFrom = null;
        if (string.Equals(value, WindowsOcr, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Windows OCR", StringComparison.OrdinalIgnoreCase))
        {
            return WindowsOcr;
        }

        if (string.Equals(value, PpOcrV5Multilingual, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "PP-OCRv5", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "PP-OCRv5 多语言版", StringComparison.OrdinalIgnoreCase))
        {
            return PpOcrV5Multilingual;
        }

        if (!string.IsNullOrWhiteSpace(value)
            && (LegacyNonWindowsEngines.Contains(value) || !string.Equals(value, WindowsOcr, StringComparison.OrdinalIgnoreCase)))
        {
            migratedFrom = value;
        }

        return PpOcrV5Multilingual;
    }

    public static string Normalize(string? value) => Normalize(value, out _);
}

public static class OcrLanguages
{
    public const string Auto = "auto";

    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        Auto,
        "ch",
        "en",
        "latin",
        "korean",
        "japan",
        "traditional_chinese",
        "eslav",
        "cyrillic",
        "th",
        "arabic",
        "devanagari",
        "ta",
        "te"
    };

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Auto;
        }

        var normalized = value.Trim().ToLowerInvariant().Replace('-', '_');
        return Supported.Contains(normalized) ? normalized : Auto;
    }
}
