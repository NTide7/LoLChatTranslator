using System.Text.RegularExpressions;

namespace LoLChatTranslator.Services;

public static class TranslationInputNormalizer
{
    public const string Version = "translation-cache-v6";

    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex IFromUsGlueRegex = new(
        @"\bifrom(?<country>us|usa)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IAmFromGlueRegex = new(
        $@"\bi(?:am|m|'m|’m)from(?<object>{CommonChatEnglish.ObjectPattern})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IAmObjectGlueRegex = new(
        $@"\bi(?:am|m|'m|’m)(?<object>{CommonChatEnglish.ObjectPattern})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ButIGlueRegex = new(
        @"\bbuti\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ButINotLikeGlueRegex = new(
        $@"\bbutinotlike(?<object>{CommonChatEnglish.ObjectPattern})?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ButINotLikePhraseRegex = new(
        $@"\bbut\s+i\s+not\s+like\s+(?<object>{CommonChatEnglish.ObjectPattern})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ButILikeGlueRegex = new(
        $@"\bbutilike(?<object>{CommonChatEnglish.ObjectPattern})?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IAlsoLikeGlueRegex = new(
        @"\bialso[mn]?like",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IAlsoMLikeSpacedRegex = new(
        @"\bi\s+also\s+[mn]\s+like\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IAlsoGlueRegex = new(
        @"\bialso\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ILikeGlueRegex = new(
        @"\bilike\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ILikeObjectGlueRegex = new(
        $@"\bilike(?<object>{CommonChatEnglish.ObjectPattern})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NotLikeObjectGlueRegex = new(
        $@"\bnotlike(?<object>{CommonChatEnglish.ObjectPattern})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NotLikePhraseRegex = new(
        $@"\bnot\s+like\s+(?<object>{CommonChatEnglish.ObjectPattern})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IVeryLikeGlueRegex = new(
        @"\biverylike\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IVeryLikePhraseRegex = new(
        $@"\bi\s+very\s+like\s+(?<object>USA|US|China|Chinese|{CommonChatEnglish.ObjectPattern}|app\s+inc)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DoYouLikeGlueRegex = new(
        @"\bdoyoulike\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DoYouLikeObjectGlueRegex = new(
        $@"\bdoyoulike(?<object>{CommonChatEnglish.ObjectPattern})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AppIncGlueRegex = new(
        @"\bappinc\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ChineseKungFuVariantRegex = new(
        @"\b(?:chinese\s+(?:k\s*ong\s*fu|kon\s*gfu|kong\s*fu|kongfu|kung\s*fu|kungfu)|chinesek\s*ong\s*fu)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex KungFuVariantRegex = new(
        @"\b(?:k\s*ong\s*fu|kon\s*gfu|kong\s*fu|kongfu|kung\s*fu|kungfu)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ChineseFoodContextGlueRegex = new(
        @"\bchinese(?<context>food|cuisine|dish|dishes|restaurant|restaurants|meal|meals)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LikeChineseLanguageRegex = new(
        @"\b(?<prefix>(?:i\s+(?:also\s+)?like|do\s+you\s+like|like))\s+chinese\b(?!\s+(?:food|cuisine|dish|dishes|restaurant|restaurants|meal|meals|kung\s+fu|k\b|kon\b|kong\b|kung\b))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string NormalizeForTranslation(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var normalized = OcrEnglishGlueFixer.FixMessageBody(OcrTextFixer.NormalizeReadableSpacing(text));
        normalized = RestoreCommonEnglishGlue(normalized);
        normalized = IVeryLikePhraseRegex.Replace(normalized, match =>
            $"i like {NormalizePhraseObject(match.Groups["object"].Value)} very much");
        normalized = ChineseFoodContextGlueRegex.Replace(normalized, match =>
            $"Chinese {match.Groups["context"].Value}");
        normalized = ChineseKungFuVariantRegex.Replace(normalized, "Chinese kung fu");
        normalized = KungFuVariantRegex.Replace(normalized, "kung fu");
        normalized = LikeChineseLanguageRegex.Replace(normalized, match =>
            $"{match.Groups["prefix"].Value} Chinese language");
        normalized = NormalizeCountryNames(normalized);
        return NormalizeSpaces(normalized);
    }

    public static string PostProcessTranslation(
        string normalizedSourceText,
        string translatedText,
        string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(translatedText)
            || !TranslatorLanguage.IsAnyChinese(targetLanguage))
        {
            return translatedText;
        }

        var result = translatedText;
        if (ContainsChineseKungFuHint(normalizedSourceText))
        {
            var kungFuText = TranslatorLanguage.IsTraditionalChinese(targetLanguage)
                ? "中國功夫"
                : "中国功夫";
            result = result.Replace("中文功夫", kungFuText, StringComparison.Ordinal);
        }

        if (ContainsChineseLanguageHint(normalizedSourceText)
            && !ContainsChineseFoodContext(normalizedSourceText))
        {
            result = result
                .Replace("中国菜", "中文", StringComparison.Ordinal)
                .Replace("中國菜", "中文", StringComparison.Ordinal);
        }

        return result;
    }

    private static bool ContainsChineseKungFuHint(string text)
    {
        return text.Contains("Chinese kung fu", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsChineseLanguageHint(string text)
    {
        return text.Contains("Chinese language", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsChineseFoodContext(string text)
    {
        return Regex.IsMatch(
            text,
            @"\bChinese\s+(?:food|cuisine|dish|dishes|restaurant|restaurants|meal|meals)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string RestoreCommonEnglishGlue(string value)
    {
        var normalized = IFromUsGlueRegex.Replace(value, match =>
        {
            var country = match.Groups["country"].Value.Equals("usa", StringComparison.OrdinalIgnoreCase)
                ? "USA"
                : "US";
            return $"i from {country}";
        });
        normalized = IAmFromGlueRegex.Replace(normalized, match =>
            $"i am from {NormalizePhraseObject(match.Groups["object"].Value)}");
        normalized = IAmObjectGlueRegex.Replace(normalized, match =>
            $"i am {NormalizePhraseObject(match.Groups["object"].Value)}");
        normalized = ButINotLikeGlueRegex.Replace(normalized, match =>
            AppendOptionalObject("but i do not like", match.Groups["object"].Value));
        normalized = ButINotLikePhraseRegex.Replace(normalized, match =>
            AppendOptionalObject("but i do not like", match.Groups["object"].Value));
        normalized = ButILikeGlueRegex.Replace(normalized, match =>
            AppendOptionalObject("but i like", match.Groups["object"].Value));
        normalized = ButIGlueRegex.Replace(normalized, "but i");
        normalized = IAlsoLikeGlueRegex.Replace(normalized, match =>
            match.Index + match.Length < normalized.Length && char.IsLetter(normalized[match.Index + match.Length])
                ? "i also like "
                : "i also like");
        normalized = IAlsoMLikeSpacedRegex.Replace(normalized, "i also like");
        normalized = IAlsoGlueRegex.Replace(normalized, "i also");
        normalized = IVeryLikeGlueRegex.Replace(normalized, "i very like");
        normalized = ILikeObjectGlueRegex.Replace(normalized, match =>
            AppendOptionalObject("i like", match.Groups["object"].Value));
        normalized = ILikeGlueRegex.Replace(normalized, "i like");
        normalized = NotLikeObjectGlueRegex.Replace(normalized, match =>
            AppendOptionalObject("not like", match.Groups["object"].Value));
        normalized = NotLikePhraseRegex.Replace(normalized, match =>
            AppendOptionalObject("not like", match.Groups["object"].Value));
        normalized = DoYouLikeObjectGlueRegex.Replace(normalized, match =>
            AppendOptionalObject("do you like", match.Groups["object"].Value));
        normalized = DoYouLikeGlueRegex.Replace(normalized, "do you like");
        normalized = AppIncGlueRegex.Replace(normalized, "app inc");
        return NormalizeSpaces(normalized);
    }

    private static string AppendOptionalObject(string prefix, string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? prefix
            : $"{prefix} {value}";
    }

    private static string NormalizePhraseObject(string value)
    {
        if (value.Equals("app inc", StringComparison.OrdinalIgnoreCase))
        {
            return "app inc";
        }

        return value.ToLowerInvariant() switch
        {
            "usa" => "USA",
            "us" => "US",
            _ => CommonChatEnglish.NormalizeObjectOrOriginal(value)
        };
    }

    private static string NormalizeCountryNames(string value)
    {
        var normalized = Regex.Replace(
            value,
            @"\bchina\b",
            "China",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(
            normalized,
            @"\b(?<prefix>from\s+)(?<country>us|usa)\b",
            match => $"{match.Groups["prefix"].Value}{(match.Groups["country"].Value.Equals("usa", StringComparison.OrdinalIgnoreCase) ? "USA" : "US")}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string NormalizeSpaces(string value)
    {
        return MultiSpaceRegex.Replace(value.Trim(), " ");
    }
}
