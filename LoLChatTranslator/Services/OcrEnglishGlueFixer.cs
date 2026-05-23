using System.Text.RegularExpressions;

namespace LoLChatTranslator.Services;

public static class OcrEnglishGlueFixer
{
    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex IAmFromGlueRegex = new(
        $@"\bi(?:am|m|'m|’m)from(?<place>{CommonChatEnglish.ObjectPattern})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IAmObjectGlueRegex = new(
        $@"\bi(?:am|m|'m|’m)(?<object>{CommonChatEnglish.ObjectPattern})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IFromGlueRegex = new(
        $@"\bifrom(?<place>{CommonChatEnglish.ObjectPattern})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IVeryLikeGlueRegex = new(
        $@"\biverylike(?<object>{CommonChatEnglish.ObjectPattern})?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BecauseIsVeryRedRegex = new(
        @"\bbecauseisveryred\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IsVeryRedRegex = new(
        @"\bisveryred\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ButINotLikeGlueRegex = new(
        $@"\bbutinotlike(?<object>{CommonChatEnglish.ObjectPattern})?(?<too>too)?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ButINotLikeSpacedObjectRegex = new(
        $@"\bbut\s+i\s+not\s+like(?<object>{CommonChatEnglish.ObjectPattern})(?<too>too)?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NotLikeGlueRegex = new(
        $@"\bnotlike(?<object>{CommonChatEnglish.ObjectPattern})(?<too>too)?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NotLikeSpacedObjectRegex = new(
        $@"\bnot\s+like(?<object>{CommonChatEnglish.ObjectPattern})(?<too>too)?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ButILikeGlueRegex = new(
        $@"\bbutilike(?<object>{CommonChatEnglish.ObjectPattern})?(?<too>too)?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ButIGlueRegex = new(
        @"\bbuti\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AndIAlsoLikeObjectGlueRegex = new(
        $@"\bandi(?:also|alsi|alsom|alson|alsol|alsoll)like(?<object>{CommonChatEnglish.ObjectPattern})(?<too>too)?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IAlsoLikeGlueRegex = new(
        @"\bi(?:also|alsi|alsom|alson|alsol|alsoll)like",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IAlsoGlueRegex = new(
        @"\bi(?:also|alsi)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ILikeObjectTooGlueRegex = new(
        $@"\bilike(?<object>{CommonChatEnglish.ObjectPattern})too\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ILikeObjectGlueRegex = new(
        $@"\bilike(?<object>appinc|{CommonChatEnglish.ObjectPattern})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ILikeGlueRegex = new(
        @"\bilike\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ILoveObjectGlueRegex = new(
        $@"\bilove(?<object>{CommonChatEnglish.ObjectPattern})(?<too>too)?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DoYouLikeObjectGlueRegex = new(
        $@"\bdoyoulike(?<object>{CommonChatEnglish.ObjectPattern})?(?<too>too)?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ObjectTooGlueRegex = new(
        $@"\b(?<object>{CommonChatEnglish.ObjectPattern})too\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AppIncGlueRegex = new(
        @"\bappinc\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GankIntRegex = new(
        @"\b(?:(?<prefix>(?:pls|plz|please)\s+gank|jg\s+gank|jungle\s+gank)\s+int|gank\s+int(?<suffix>\s+(?:pls|plz|please))?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ChineseKungFuVariantRegex = new(
        @"\b(?:chinese\s*k\s*ong\s*fu|chinese\s*k\s*ongfu|chinese\s*kon\s*gfu|chinese\s*kong\s*fu|chinese\s*kongfu|chinese\s*kung\s*fu|chinese\s*kungfu|chinesek\s*ong\s*fu|chinesek\s*ongfu|chinesekongfu|chinesekungfu)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex KungFuVariantRegex = new(
        @"\b(?:k\s*ong\s*fu|k\s*ongfu|kon\s*gfu|kong\s*fu|kongfu|kung\s*fu|kungfu|ongfu)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string FixMessageBody(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var fixedText = OcrTextFixer.NormalizeReadableSpacing(text);
        fixedText = GankIntRegex.Replace(fixedText, match =>
        {
            var prefix = match.Groups["prefix"].Value;
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                return $"{prefix} mid";
            }

            return $"gank mid{match.Groups["suffix"].Value}";
        });
        fixedText = IAmFromGlueRegex.Replace(fixedText, match => $"i am from {CommonChatEnglish.NormalizeObjectOrOriginal(match.Groups["place"].Value)}");
        fixedText = IAmObjectGlueRegex.Replace(fixedText, match => $"i am {CommonChatEnglish.NormalizeObjectOrOriginal(match.Groups["object"].Value)}");
        fixedText = IFromGlueRegex.Replace(fixedText, match => $"i from {NormalizeObject(match.Groups["place"].Value)}");
        fixedText = IVeryLikeGlueRegex.Replace(fixedText, match => AppendOptionalObject("i like", match.Groups["object"].Value, " very much"));
        fixedText = BecauseIsVeryRedRegex.Replace(fixedText, "because it is very red");
        fixedText = IsVeryRedRegex.Replace(fixedText, "it is very red");
        fixedText = ButINotLikeGlueRegex.Replace(fixedText, match => AppendOptionalObject("but i do not like", match.Groups["object"].Value, AppendToo(match)));
        fixedText = ButINotLikeSpacedObjectRegex.Replace(fixedText, match => AppendOptionalObject("but i do not like", match.Groups["object"].Value, AppendToo(match)));
        fixedText = NotLikeGlueRegex.Replace(fixedText, match => AppendOptionalObject("not like", match.Groups["object"].Value, AppendToo(match)));
        fixedText = NotLikeSpacedObjectRegex.Replace(fixedText, match => AppendOptionalObject("not like", match.Groups["object"].Value, AppendToo(match)));
        fixedText = ButILikeGlueRegex.Replace(fixedText, match => AppendOptionalObject("but i like", match.Groups["object"].Value, AppendToo(match)));
        fixedText = ButIGlueRegex.Replace(fixedText, "but i");
        fixedText = AndIAlsoLikeObjectGlueRegex.Replace(fixedText, match => AppendOptionalObject("and i also like", match.Groups["object"].Value, AppendToo(match)));
        fixedText = IAlsoLikeGlueRegex.Replace(fixedText, match =>
            match.Index + match.Length < fixedText.Length && char.IsLetter(fixedText[match.Index + match.Length])
                ? "i also like "
                : "i also like");
        fixedText = IAlsoGlueRegex.Replace(fixedText, "i also");
        fixedText = ILikeObjectTooGlueRegex.Replace(fixedText, match => AppendOptionalObject("i like", match.Groups["object"].Value, " too"));
        fixedText = ILikeObjectGlueRegex.Replace(fixedText, match => AppendOptionalObject("i like", match.Groups["object"].Value));
        fixedText = ILikeGlueRegex.Replace(fixedText, "i like");
        fixedText = ILoveObjectGlueRegex.Replace(fixedText, match => AppendOptionalObject("i love", match.Groups["object"].Value, AppendToo(match)));
        fixedText = DoYouLikeObjectGlueRegex.Replace(fixedText, match => AppendOptionalObject("do you like", match.Groups["object"].Value, AppendToo(match)));
        fixedText = ObjectTooGlueRegex.Replace(fixedText, match => AppendOptionalObject(string.Empty, match.Groups["object"].Value, " too").Trim());
        fixedText = AppIncGlueRegex.Replace(fixedText, "app inc");
        fixedText = ChineseKungFuVariantRegex.Replace(fixedText, "Chinese kung fu");
        fixedText = KungFuVariantRegex.Replace(fixedText, "kung fu");
        fixedText = NormalizeCountryNames(fixedText);
        return NormalizeSpaces(OcrTextFixer.NormalizeReadableSpacing(fixedText));
    }

    public static string NormalizeCompactForComparison(string text)
    {
        var fixedText = FixMessageBody(text);
        var chars = fixedText
            .Normalize()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray();
        return new string(chars);
    }

    public static int CountSuspiciousGlueMarkers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var regex in new[]
                 {
                     IFromGlueRegex,
                     IAmFromGlueRegex,
                     IAmObjectGlueRegex,
                     IVeryLikeGlueRegex,
                     BecauseIsVeryRedRegex,
                     IsVeryRedRegex,
                     ButINotLikeGlueRegex,
                     ButINotLikeSpacedObjectRegex,
                     NotLikeGlueRegex,
                     NotLikeSpacedObjectRegex,
                     ButILikeGlueRegex,
                     ButIGlueRegex,
                     AndIAlsoLikeObjectGlueRegex,
                     IAlsoLikeGlueRegex,
                     ILikeObjectTooGlueRegex,
                     ILikeObjectGlueRegex,
                     ILoveObjectGlueRegex,
                     DoYouLikeObjectGlueRegex,
                     ObjectTooGlueRegex,
                     ChineseKungFuVariantRegex,
                     KungFuVariantRegex
                 })
        {
            count += regex.Matches(text).Count;
        }

        return count;
    }

    private static string AppendOptionalObject(string prefix, string value, string suffix = "")
    {
        var text = string.IsNullOrWhiteSpace(value)
            ? prefix
            : $"{prefix} {NormalizeObject(value)}";
        return $"{text}{suffix}";
    }

    private static string AppendToo(Match match)
    {
        return match.Groups["too"].Success && !string.IsNullOrWhiteSpace(match.Groups["too"].Value)
            ? " too"
            : string.Empty;
    }

    private static string NormalizeObject(string value)
    {
        if (value.Equals("appinc", StringComparison.OrdinalIgnoreCase))
        {
            return "app inc";
        }

        return CommonChatEnglish.NormalizeObjectOrOriginal(value);
    }

    private static string NormalizeCountryNames(string value)
    {
        var normalized = Regex.Replace(
            value,
            @"\bchina\b",
            "China",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\bchinese\b",
            "Chinese",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\busa\b",
            "USA",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(
            normalized,
            @"\b(?<prefix>from\s+)us\b",
            match => $"{match.Groups["prefix"].Value}US",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string NormalizeSpaces(string value)
    {
        return MultiSpaceRegex.Replace(value.Trim(), " ");
    }
}
