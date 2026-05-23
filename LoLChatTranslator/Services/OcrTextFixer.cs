using System.Text;
using System.Text.RegularExpressions;

namespace LoLChatTranslator.Services;

public static class OcrTextFixer
{
    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex ChineseInnerSpaceRegex = new(
        @"(?<=[\u4e00-\u9fff])\s+(?=[\u4e00-\u9fff])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SpaceBeforePunctuationRegex = new(
        @"\s+([,.;:?!，。？！；：])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AsciiPunctuationMissingSpaceRegex = new(
        @"([,.;:?!])(?=[A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TrailingPunctuationRegex = new(@"[\?？!！\.。,\uFF0C~～]+$", RegexOptions.Compiled);
    private static readonly Regex LongAsciiTokenRegex = new(
        @"[A-Za-z]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AdjacentAsciiTokenSequenceRegex = new(
        @"(?<![A-Za-z])(?:[A-Za-z]{4,}\s+){1,3}[A-Za-z]{4,}(?![A-Za-z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AsciiWordRegex = new(
        @"[A-Za-z]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LongAsciiResidueRegex = new(
        @"[A-Za-z]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ShortFirstPersonResidueBeforeChineseRegex = new(
        @"(?:^|[^A-Za-z])(?:i|im|i'm|iam)(?=[\u4e00-\u9fff])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] FirstPersonGlueVerbs = ["like", "love", "need", "want", "have", "speak", "know", "play"];
    private static readonly HashSet<string> EnglishSegmentWords = new(StringComparer.Ordinal)
    {
        "a", "about", "adc", "after", "again", "all", "also", "am", "an", "and", "any", "are", "as", "ask", "at",
        "attack", "away", "back", "bad", "banana", "baron", "base", "be", "because", "been", "before", "best",
        "better", "blue", "bot", "bottom", "buff", "buffs", "but", "buy", "by", "can", "cant", "care",
        "careful", "carry", "cd", "champ", "chat", "china", "chinese", "clear", "come", "coming", "cooldown",
        "could", "couldnt", "cover", "danger", "def", "defend", "did", "didnt", "do", "does", "doesnt",
        "doing", "done",
        "dont", "drake", "dragon", "easy", "enemy", "enemies", "english", "farm", "fast", "feed", "feeding",
        "engine", "engines", "fight", "first", "flash", "focus", "for", "freeze", "from", "game", "gank", "get", "gg", "give",
        "gl", "go", "going", "good", "got", "group", "hard", "has", "hasnt", "had", "hadnt", "have", "havent", "he", "hello",
        "help", "herald", "here", "hey", "hf", "hi", "him", "his", "how", "hp", "i", "if", "im", "in", "into",
        "is", "it", "its", "ja", "japanese", "jg", "jungle", "just", "kill", "know", "ko", "kon", "kongfu", "korean", "kungfu", "lane",
        "laner", "last", "late", "less", "like", "lol", "look", "lose", "lost", "love", "low", "mana", "me",
        "many", "mb", "mid", "middle", "mine", "miss", "missing", "more", "my", "need", "nexus", "nice", "no", "not",
        "now", "np", "of", "ok", "okay", "on", "one", "or", "our", "out", "panda", "place", "places", "play", "played", "playing",
        "please", "pls", "plz", "push", "recall", "red", "river", "roam", "safe", "save", "say", "scale",
        "scaling", "see", "she", "should", "shouldnt", "so", "some", "sorry", "speak", "split", "start", "still",
        "stop", "strong", "supp", "support", "take", "team", "teammate", "teammates", "than", "thank",
        "thanks", "that", "the", "their", "them", "then", "there", "these", "they", "think", "this", "those",
        "three", "to", "together", "too", "top", "tower", "tp", "try", "trying", "turret", "two", "ty", "u",
        "ult", "ulti", "us", "very", "vi", "vietnamese", "vision", "wait", "want", "ward", "wards", "was",
        "we", "weak", "were", "what", "when", "where", "who", "why", "will", "win", "with", "without",
        "won", "wont", "worse", "would", "wouldnt", "wp", "yes", "you", "your", "yours"
    };
    private static readonly Regex FirstPersonGlueRegex = new(
        @"^i(like|love|need|want|have|speak|know|play)([a-z]{2,})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex QuestionGlueRegex = new(
        @"^do(you|u)(like|love|need|want|have|speak|know|play)([a-z]{2,})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CanYouGlueRegex = new(
        @"^can(you|u)(help|gank|come|speak|play)([a-z]{0,})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string ApplyBuiltInFixes(string value)
    {
        var normalized = OcrEnglishGlueFixer.FixMessageBody(NormalizeReadableSpacing(value));
        var key = NormalizeKey(normalized);
        var canonicalKey = NormalizeFirstPersonOcrConfusions(key);

        var exact = canonicalKey switch
        {
            "halo" or "helo" or "hell0" => "hello",
            "0k" => "ok",
            "ilikechina" => "i like china",
            "ilikechinese" => "i like chinese",
            "ilovechina" => "i love china",
            "doyoulikechina" or "doulikechina" => "do you like china",
            "doyoulikechinese" or "doulikechinese" => "do you like chinese",
            "canyouhelpme" or "canuhelpme" => "can you help me",
            _ => null
        };

        if (exact is not null)
        {
            return exact;
        }

        if (TryFixFirstPersonGlue(canonicalKey, out var firstPersonGlue))
        {
            return firstPersonGlue;
        }

        if (TrySegmentGluedEnglishTokens(normalized, out var segmented))
        {
            return segmented;
        }

        if (normalized.Contains(' ') || !IsAsciiWord(key))
        {
            return normalized;
        }

        if (QuestionGlueRegex.Match(canonicalKey) is { Success: true } questionMatch)
        {
            return NormalizeSpaces($"do you {questionMatch.Groups[2].Value} {questionMatch.Groups[3].Value}");
        }

        if (CanYouGlueRegex.Match(canonicalKey) is { Success: true } canYouMatch)
        {
            return NormalizeSpaces($"can you {canYouMatch.Groups[2].Value} {canYouMatch.Groups[3].Value}");
        }

        if (FirstPersonGlueRegex.Match(canonicalKey) is { Success: true } firstPersonMatch)
        {
            return NormalizeSpaces($"i {firstPersonMatch.Groups[1].Value} {firstPersonMatch.Groups[2].Value}");
        }

        return normalized;
    }

    private static bool TrySegmentGluedEnglishTokens(string value, out string segmented)
    {
        var changed = false;
        segmented = AdjacentAsciiTokenSequenceRegex.Replace(value, match =>
        {
            if (!TrySegmentAdjacentGluedEnglishTokens(match.Value, out var replacement))
            {
                return match.Value;
            }

            changed = true;
            return replacement;
        });

        segmented = LongAsciiTokenRegex.Replace(segmented, match =>
        {
            if (!TrySegmentGluedEnglishToken(match.Value, out var replacement))
            {
                return match.Value;
            }

            changed = true;
            return replacement;
        });

        if (changed)
        {
            segmented = NormalizeSpaces(segmented);
        }

        return changed;
    }

    private static bool TrySegmentAdjacentGluedEnglishTokens(string value, out string segmented)
    {
        segmented = string.Empty;
        var tokens = AsciiWordRegex.Matches(value)
            .Select(match => match.Value)
            .ToList();
        if (tokens.Count < 2 || tokens.All(token => EnglishSegmentWords.Contains(token.ToLowerInvariant())))
        {
            return false;
        }

        var compact = string.Concat(tokens);
        if (compact.Length < 8 || !TrySegmentGluedEnglishToken(compact, out var compactSegmented))
        {
            return false;
        }

        var segmentedWords = compactSegmented.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (segmentedWords.Length <= tokens.Count)
        {
            return false;
        }

        segmented = compactSegmented;
        return true;
    }

    private static bool TrySegmentGluedEnglishToken(string token, out string segmented)
    {
        segmented = string.Empty;
        var lower = token.ToLowerInvariant();
        if (EnglishSegmentWords.Contains(lower))
        {
            return false;
        }

        var length = lower.Length;
        var scores = Enumerable.Repeat(int.MinValue / 4, length + 1).ToArray();
        var nextLengths = new int[length];
        scores[length] = 0;

        for (var start = length - 1; start >= 0; start--)
        {
            var maxWordLength = Math.Min(12, length - start);
            for (var wordLength = 1; wordLength <= maxWordLength; wordLength++)
            {
                var word = lower.Substring(start, wordLength);
                if (!EnglishSegmentWords.Contains(word) || scores[start + wordLength] <= int.MinValue / 8)
                {
                    continue;
                }

                var score = scores[start + wordLength] + wordLength * wordLength;
                if (wordLength <= 2)
                {
                    score -= 8;
                }

                if (score <= scores[start])
                {
                    continue;
                }

                scores[start] = score;
                nextLengths[start] = wordLength;
            }
        }

        if (scores[0] <= int.MinValue / 8)
        {
            return false;
        }

        var words = new List<string>();
        for (var index = 0; index < length;)
        {
            var wordLength = nextLengths[index];
            if (wordLength <= 0)
            {
                return false;
            }

            words.Add(lower.Substring(index, wordLength));
            index += wordLength;
        }

        if (words.Count < 2 || words.Count(word => word.Length <= 2) > words.Count / 2)
        {
            return false;
        }

        var averageWordLength = words.Average(word => word.Length);
        var coverageScore = words.Sum(word => word.Length >= 3 ? word.Length : 0) / (double)Math.Max(1, lower.Length);
        if (averageWordLength < 2.75 || coverageScore < 0.72)
        {
            return false;
        }

        segmented = string.Join(" ", words);
        return true;
    }

    public static string NormalizeReadableSpacing(string value)
    {
        var normalized = NormalizeSpaces(value).Normalize(NormalizationForm.FormKC);
        normalized = ChineseInnerSpaceRegex.Replace(normalized, string.Empty);
        normalized = SpaceBeforePunctuationRegex.Replace(normalized, "$1");
        normalized = AsciiPunctuationMissingSpaceRegex.Replace(normalized, "$1 ");
        return NormalizeSpaces(normalized);
    }

    public static bool TryTranslateBuiltInPhrase(string text, string targetLanguage, out string translatedText)
    {
        translatedText = string.Empty;
        if (!TranslatorLanguage.IsAnyChinese(targetLanguage))
        {
            return false;
        }

        var normalized = ApplyBuiltInFixes(text);
        var key = TrailingPunctuationRegex.Replace(NormalizeSpaces(normalized).ToLowerInvariant(), string.Empty).Trim();
        var simplified = TryBuildSimpleChineseFallback(key) ?? key switch
        {
            "hello" or "hi" => "你好",
            "im chinese" or "i'm chinese" or "i am chinese" => "我是中国人",
            "im from china" or "i'm from china" or "i am from china" => "我来自中国",
            "im from usa" or "i'm from usa" or "i am from usa"
                or "im from us" or "i'm from us" or "i am from us" => "我来自美国",
            "pls gank mid" or "plz gank mid" or "gank mid pls" or "gank mid plz"
                or "please gank mid" or "please gank middle" or "gank mid please"
                or "please gank mid lane" or "jg gank mid" or "jungle gank mid"
                or "gank mid" or "gank middle" or "gank middle pls" or "gank middle plz"
                or "come mid" or "help mid" => "请来中路抓一下",
            "ff" or "/ff" or "surrender" => "请求投降",
            "ff15" or "ff 15" => "请求15分钟投降",
            "ff20" or "ff 20" => "请求20分钟投降",
            "help me" => "帮我",
            "ok" or "okay" => "好",
            "i like china" => "我喜欢中国",
            "i like china too" => "我也喜欢中国",
            "and i like china" => "我也喜欢中国",
            "and i like china too" => "我也喜欢中国",
            "i love china" => "我爱中国",
            "i like chinese" => "我喜欢中文",
            "i like chinese too" => "我也喜欢中文",
            "and i like chinese" => "我也喜欢中文",
            "and i like chinese too" => "我也喜欢中文",
            "do you like china" => "你喜欢中国吗？",
            "do you like chinese" => "你喜欢中文吗？",
            "can you help me" => "你能帮我吗？",
            _ => null
        };

        if (simplified is null)
        {
            return false;
        }

        translatedText = TranslatorLanguage.IsTraditionalChinese(targetLanguage)
            ? ToTraditionalChinese(simplified)
            : simplified;
        return true;
    }

    private static string? TryBuildSimpleChineseFallback(string key)
    {
        if (CommonChatEnglish.TryGetSimplifiedChineseObject(key, personContext: false, out var directObject))
        {
            return directObject;
        }

        var normalized = key
            .Replace("i'm", "im", StringComparison.OrdinalIgnoreCase)
            .Replace("i am", "i am", StringComparison.OrdinalIgnoreCase)
            .Trim();

        var iAmChinese = Regex.Match(
            normalized,
            @"^(?:im|i\s+am)\s+chinese$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (iAmChinese.Success)
        {
            return "我是中国人";
        }

        var fromMatch = Regex.Match(
            normalized,
            $@"^(?:im|i\s+am|i)\s+from\s+(?<object>{CommonChatEnglish.ObjectPattern})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (fromMatch.Success
            && CommonChatEnglish.TryGetSimplifiedChineseObject(fromMatch.Groups["object"].Value, personContext: false, out var place))
        {
            return $"我来自{place}";
        }

        var objectPattern = $"(?:{CommonChatEnglish.ObjectPattern})";
        var likeMatch = Regex.Match(
            normalized,
            $@"^(?:(?<but>but)\s+|(?<and>and)\s+)?(?:i\s+(?<also>also)\s+like|i\s+like)\s+(?<objects>{objectPattern}(?:\s+and\s+{objectPattern})?)(?<too>\s+too)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (likeMatch.Success
            && TryTranslateObjectList(likeMatch.Groups["objects"].Value, out var likeObject))
        {
            if (likeMatch.Groups["but"].Success)
            {
                return $"但我喜欢{likeObject}";
            }

            return likeMatch.Groups["too"].Success || likeMatch.Groups["also"].Success || likeMatch.Groups["and"].Success
                ? $"我也喜欢{likeObject}"
                : $"我喜欢{likeObject}";
        }

        var loveMatch = Regex.Match(
            normalized,
            $@"^i\s+love\s+(?<object>{CommonChatEnglish.ObjectPattern})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (loveMatch.Success
            && CommonChatEnglish.TryGetSimplifiedChineseObject(loveMatch.Groups["object"].Value, personContext: false, out var loveObject))
        {
            return $"我爱{loveObject}";
        }

        var dislikeMatch = Regex.Match(
            normalized,
            $@"^(?<but>but\s+)?i\s+do\s+not\s+like\s+(?<object>{CommonChatEnglish.ObjectPattern})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (dislikeMatch.Success
            && CommonChatEnglish.TryGetSimplifiedChineseObject(dislikeMatch.Groups["object"].Value, personContext: false, out var dislikeObject))
        {
            return dislikeMatch.Groups["but"].Success
                ? $"但我不喜欢{dislikeObject}"
                : $"我不喜欢{dislikeObject}";
        }

        var oldDislikeMatch = Regex.Match(
            normalized,
            $@"^(?<but>but\s+)?i\s+not\s+like\s+(?<object>{CommonChatEnglish.ObjectPattern})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (oldDislikeMatch.Success
            && CommonChatEnglish.TryGetSimplifiedChineseObject(oldDislikeMatch.Groups["object"].Value, personContext: false, out var oldDislikeObject))
        {
            return oldDislikeMatch.Groups["but"].Success
                ? $"但我不喜欢{oldDislikeObject}"
                : $"我不喜欢{oldDislikeObject}";
        }

        var clauses = Regex.Split(normalized, @"\s*,\s*")
            .Select(clause => clause.Trim())
            .Where(clause => clause.Length > 0)
            .ToList();
        if (clauses.Count >= 2)
        {
            var translatedClauses = new List<string>();
            foreach (var clause in clauses)
            {
                var translatedClause = TryBuildSimpleChineseFallback(clause);
                if (string.IsNullOrWhiteSpace(translatedClause))
                {
                    translatedClauses.Clear();
                    break;
                }

                translatedClauses.Add(translatedClause);
            }

            if (translatedClauses.Count == clauses.Count)
            {
                return string.Join("，", translatedClauses);
            }
        }

        return null;
    }

    private static bool TryTranslateObjectList(string value, out string translated)
    {
        translated = string.Empty;
        var parts = Regex.Split(value, @"\s+and\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();
        if (parts.Count == 0)
        {
            return false;
        }

        var translatedParts = new List<string>();
        foreach (var part in parts)
        {
            if (!CommonChatEnglish.TryGetSimplifiedChineseObject(part, personContext: false, out var item))
            {
                return false;
            }

            translatedParts.Add(item);
        }

        translated = string.Join("和", translatedParts);
        return true;
    }

    public static bool LooksUntranslated(string sourceText, string translatedText, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(sourceText)
            || string.IsNullOrWhiteSpace(translatedText)
            || !ContainsTranslatableText(sourceText))
        {
            return false;
        }

        var target = TranslatorLanguage.NormalizeTargetLanguage(targetLanguage);
        if (TranslatorLanguage.IsAnyChinese(target))
        {
            if (HasLikelyChineseTargetEnglishResidue(sourceText, translatedText))
            {
                return true;
            }

            if (IsMostlyChineseSourceForChineseTarget(sourceText))
            {
                return false;
            }
        }

        if (LooksLikeTargetLanguage(translatedText, target))
        {
            return false;
        }

        var sourceKey = NormalizeKey(ApplyBuiltInFixes(sourceText));
        var translatedKey = NormalizeKey(ApplyBuiltInFixes(translatedText));
        if (sourceKey.Length < 3 || translatedKey.Length < 3)
        {
            return false;
        }

        return sourceKey.Equals(translatedKey, StringComparison.OrdinalIgnoreCase)
            || translatedKey.Contains(sourceKey, StringComparison.OrdinalIgnoreCase)
            || sourceKey.Contains(translatedKey, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasSuspiciousEnglishResidueForChineseTarget(string translatedText)
    {
        if (string.IsNullOrWhiteSpace(translatedText))
        {
            return false;
        }

        if (LongAsciiResidueRegex.IsMatch(translatedText))
        {
            return true;
        }

        if (ShortFirstPersonResidueBeforeChineseRegex.IsMatch(translatedText.Normalize(NormalizationForm.FormKC)))
        {
            return true;
        }

        var words = AsciiWordRegex.Matches(translatedText)
            .Select(match => match.Value)
            .Where(word => word.Length >= 2 && !IsAllowedShortGameTerm(word))
            .ToList();
        var asciiLetters = words.Sum(word => word.Length);
        return words.Count >= 3 && asciiLetters >= 12;
    }

    private static bool ContainsTranslatableText(string value)
    {
        return value.Any(ch =>
            char.IsAsciiLetter(ch)
            || ch is >= '\u4e00' and <= '\u9fff'
            || ch is >= '\u3040' and <= '\u30ff'
            || ch is >= '\uac00' and <= '\ud7af');
    }

    private static bool LooksLikeTargetLanguage(string value, string targetLanguage)
    {
        return targetLanguage switch
        {
            "zh-Hans" or "zh-Hant" => value.Any(ch => ch is >= '\u4e00' and <= '\u9fff'),
            "ko" => value.Any(ch => ch is >= '\uac00' and <= '\ud7af'),
            "ja" => value.Any(ch => ch is >= '\u3040' and <= '\u30ff' || ch is >= '\u4e00' and <= '\u9fff'),
            "en" => value.Any(char.IsAsciiLetter),
            _ => false
        };
    }

    private static string NormalizeKey(string value)
    {
        return Regex.Replace(value.ToLowerInvariant(), @"[\s\p{P}\p{S}]+", string.Empty);
    }

    private static bool HasLikelyChineseTargetEnglishResidue(string sourceText, string translatedText)
    {
        if (!sourceText.Any(char.IsAsciiLetter))
        {
            return HasSuspiciousEnglishResidueForChineseTarget(translatedText);
        }

        if (HasSuspiciousEnglishResidueForChineseTarget(translatedText))
        {
            return true;
        }

        var translatedAsciiCompact = NormalizeAsciiLettersOnly(translatedText);
        if (translatedAsciiCompact.Length < 8)
        {
            return false;
        }

        foreach (var fragment in BuildSourceEnglishFragments(sourceText))
        {
            if (fragment.Length >= 8
                && translatedAsciiCompact.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> BuildSourceEnglishFragments(string sourceText)
    {
        var fixedSource = ApplyBuiltInFixes(sourceText);
        var words = AsciiWordRegex.Matches(fixedSource)
            .Select(match => match.Value.ToLowerInvariant())
            .Where(word => word.Length >= 2 && !IsAllowedShortGameTerm(word))
            .ToList();

        for (var start = 0; start < words.Count; start++)
        {
            var builder = new StringBuilder();
            for (var end = start; end < words.Count && end < start + 5; end++)
            {
                builder.Append(words[end]);
                if (builder.Length >= 8)
                {
                    yield return builder.ToString();
                }
            }
        }
    }

    private static bool IsMostlyChineseSourceForChineseTarget(string sourceText)
    {
        var chineseCount = sourceText.Count(ch => ch is >= '\u4e00' and <= '\u9fff');
        if (chineseCount == 0)
        {
            return false;
        }

        var asciiLetterCount = sourceText.Count(char.IsAsciiLetter);
        return asciiLetterCount <= Math.Max(6, chineseCount * 2)
            && !LongAsciiResidueRegex.IsMatch(sourceText)
            && !BuildSourceEnglishFragments(sourceText).Any(fragment => fragment.Length >= 12);
    }

    private static string NormalizeAsciiLettersOnly(string value)
    {
        var chars = value
            .Normalize(NormalizationForm.FormKC)
            .Where(char.IsAsciiLetter)
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars);
    }

    private static bool IsAllowedShortGameTerm(string value)
    {
        var token = value.Trim().ToLowerInvariant();
        return token is "adc" or "ap" or "ad" or "aoe" or "baron" or "bot" or "buff" or "cd" or "cs"
            or "drake" or "ff" or "flash" or "gank" or "gg" or "jg" or "jgl" or "jungle" or "kda"
            or "lol" or "mid" or "mia" or "ss" or "top" or "tp" or "ult" or "ulti" or "ward";
    }

    private static string NormalizeSpaces(string value)
    {
        return MultiSpaceRegex.Replace(value.Trim(), " ");
    }

    private static bool IsAsciiWord(string value)
    {
        return value.Length > 0 && value.All(ch => ch is >= 'a' and <= 'z');
    }

    private static string NormalizeFirstPersonOcrConfusions(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        var normalized = key.ToLowerInvariant();
        normalized = normalized switch
        {
            "iikechina" => "ilikechina",
            "iikechinese" => "ilikechinese",
            "iovechina" => "ilovechina",
            _ => normalized
        };

        var prefix = normalized.StartsWith("and", StringComparison.Ordinal) ? "and" : string.Empty;
        var body = prefix.Length > 0 ? normalized[prefix.Length..] : normalized;
        foreach (var verb in FirstPersonGlueVerbs)
        {
            if (body.StartsWith($"l{verb}", StringComparison.Ordinal)
                || body.StartsWith($"1{verb}", StringComparison.Ordinal))
            {
                return $"{prefix}i{body[1..]}";
            }
        }

        return normalized;
    }

    private static bool TryFixFirstPersonGlue(string key, out string fixedText)
    {
        fixedText = string.Empty;
        var prefix = key.StartsWith("and", StringComparison.Ordinal) ? "and" : string.Empty;
        var body = prefix.Length > 0 ? key[prefix.Length..] : key;
        foreach (var verb in FirstPersonGlueVerbs)
        {
            var marker = $"i{verb}";
            if (!body.StartsWith(marker, StringComparison.Ordinal))
            {
                continue;
            }

            var target = body[marker.Length..];
            if (target.Length < 2)
            {
                continue;
            }

            var suffix = string.Empty;
            if (target.EndsWith("too", StringComparison.Ordinal) && target.Length > 5)
            {
                target = target[..^3];
                suffix = " too";
            }

            var leading = prefix.Length > 0 ? "and " : string.Empty;
            fixedText = NormalizeSpaces($"{leading}i {verb} {target}{suffix}");
            return true;
        }

        return false;
    }

    private static string ToTraditionalChinese(string simplified)
    {
        return simplified switch
        {
            "来抓中" => "來抓中",
            "帮我" => "幫我",
            "苹果" => "蘋果",
            "香蕉" => "香蕉",
            "熊猫" => "熊貓",
            "中国" => "中國",
            "美国" => "美國",
            "我喜欢中国" => "我喜歡中國",
            "我也喜欢中国" => "我也喜歡中國",
            "我爱中国" => "我愛中國",
            "我来自中国" => "我來自中國",
            "我来自美国" => "我來自美國",
            "我是中国人" => "我是中國人",
            "我喜欢中文" => "我喜歡中文",
            "我也喜欢中文" => "我也喜歡中文",
            "我喜欢苹果" => "我喜歡蘋果",
            "我也喜欢苹果" => "我也喜歡蘋果",
            "我喜欢香蕉" => "我喜歡香蕉",
            "我也喜欢香蕉" => "我也喜歡香蕉",
            "我喜欢熊猫" => "我喜歡熊貓",
            "我也喜欢熊猫" => "我也喜歡熊貓",
            "我喜欢美国" => "我喜歡美國",
            "我不喜欢苹果" => "我不喜歡蘋果",
            "我不喜欢香蕉" => "我不喜歡香蕉",
            "但我不喜欢苹果" => "但我不喜歡蘋果",
            "但我不喜欢香蕉" => "但我不喜歡香蕉",
            "你喜欢中国吗？" => "你喜歡中國嗎？",
            "你喜欢中文吗？" => "你喜歡中文嗎？",
            "你能帮我吗？" => "你能幫我嗎？",
            _ => simplified
        };
    }
}
