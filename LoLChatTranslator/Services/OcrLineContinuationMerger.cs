using System.Text.RegularExpressions;
using System.Text;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public static class OcrLineContinuationMerger
{
    private static readonly Regex PlayerChatLineRegex = new(
        @"^\s*(?:(?<timestamp>\d{1,3}\s*[:：]\s*\d{2})\s*)?[\[【［](?<channel>[^\]】］]+)[\]】］]\s*(?<sender>.+?)\s*[（(]\s*(?<champion>[^）)]+?)\s*[）)]\s*[:：]\s*(?<message>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ChannelPrefixRegex = new(
        @"^\s*(?:(?<timestamp>\d{1,3}\s*[:：]\s*\d{2})\s*)?[\[【［](?<channel>[^\]】］]+)[\]】］]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TimestampPrefixRegex = new(
        @"^\s*\d{1,3}\s*[:：]\s*\d{2}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FuzzyTimestampPrefixRegex = new(
        @"^\s*[0-9sS]{1,3}\s*(?:[:：]|\s)\s*[0-9oO]{2}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TimestampChampionEventRegex = new(
        @"^\s*\d{1,3}\s*[:：]\s*\d{2}\s*[^:：\[\]【】［］]+[（(][^）)]+[）)]\s*[^:：]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly string[] ContinuationBlockerKeywords =
    [
        "任务完成",
        "任務完成",
        "守卫任务完成",
        "守衛任務完成",
        "守卫",
        "守衛",
        "已选择了位置",
        "已经选择了位置",
        "已选择位置",
        "已選擇了位置",
        "已經選擇了位置",
        "购买了",
        "已购买",
        "購買了",
        "已購買",
        "正在路上",
        "正在请求协助",
        "正在請求協助",
        "请求协助",
        "請求協助",
        "警告",
        "撤退",
        "敌人不见了",
        "敵人不見了",
        "已阵亡",
        "已陣亡",
        "已击杀",
        "已擊殺",
        "已经击杀",
        "已經擊殺",
        "摧毁了",
        "摧毀了",
        "获得了",
        "獲得了",
        "重新连接",
        "重新連接",
        "断开连接",
        "斷開連接",
        "你已临近训练模式自由的最大游戏时长",
        "你已临近训练模式",
        "已临近",
        "最大游戏时长",
        "训练模式自由的最大游戏时长",
        "训练模式",
        "游戏将在",
        "分钟内结束",
        "游戏将在5分钟内结束",
        "你已臨近訓練模式",
        "最大遊戲時長",
        "遊戲將在",
        "分鐘內結束",
        "practice tool",
        "game will end in",
        "maximum game time",
        "training mode",
        "maximum duration",
        "isontheway",
        "enemymissing",
        "signals",
        "purchased",
        "hasbeenslain",
        "hasslain",
        "reconnected",
        "disconnected"
    ];

    private static readonly HashSet<string> KnownSplitWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "apple",
        "because",
        "chinese",
        "dragon",
        "jungle",
        "kongfu",
        "kungfu",
        "middle",
        "please",
        "support"
    };

    private static readonly HashSet<string> StandaloneShortMessages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ff",
        "/ff",
        "gg",
        "ggwp",
        "hello",
        "hi",
        "ok",
        "pls gank mid",
        "please gank mid",
        "gank mid",
        "wp"
    };

    private static readonly HashSet<string> CommonIndependentWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "but", "do", "for", "ff", "gg", "good", "hello", "i", "in", "is", "it",
        "job", "like", "me", "mid", "nice", "no", "not", "of", "ok", "on", "or", "pls", "please", "the",
        "to", "too", "try", "u", "we", "wp", "you"
    };

    public static OcrLineMergeResult Merge(
        IReadOnlyList<string> lines,
        IReadOnlyList<OcrTextLine>? textLines = null)
    {
        if (lines.Count == 0)
        {
            return new OcrLineMergeResult([], 0, 0, [], []);
        }

        var orderedLines = BuildInputs(lines, textLines);
        var observedRightEdge = CalculateObservedRightEdge(orderedLines);
        var merged = new List<MergedLine>();
        var events = new List<OcrLineMergeEvent>();
        int? lastPlayerLineIndex = null;

        foreach (var input in orderedLines)
        {
            var text = NormalizeLine(input.Text);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (IsFullPlayerChatLine(text))
            {
                merged.Add(new MergedLine(text, input));
                lastPlayerLineIndex = merged.Count - 1;
                continue;
            }

            if (IsContinuationBlocker(text))
            {
                merged.Add(new MergedLine(text, input));
                lastPlayerLineIndex = null;
                continue;
            }

            if (lastPlayerLineIndex is int previousIndex
                && IsContinuation(text, input, merged[previousIndex]))
            {
                var previousText = merged[previousIndex].Text;
                var joined = JoinContinuation(
                    previousText,
                    text,
                    merged[previousIndex].LastInput,
                    input,
                    observedRightEdge,
                    out var splitJoin);
                merged[previousIndex].WithContinuation(joined, input);
                events.Add(new OcrLineMergeEvent(
                    input.OriginalIndex,
                    previousText,
                    text,
                    joined,
                    splitJoin?.LeftToken,
                    splitJoin?.RightToken,
                    splitJoin?.JoinedToken,
                    splitJoin?.WasNearRightEdge ?? false));
                continue;
            }

            merged.Add(new MergedLine(text, input));
            lastPlayerLineIndex = null;
        }

        var mergedLines = merged
            .Select(line => line.ToMergedOcrLine())
            .OrderBy(line => line.VisualOrder)
            .ThenBy(line => line.Top ?? double.MaxValue)
            .ThenBy(line => line.Left ?? double.MaxValue)
            .ToList();
        var resultLines = mergedLines.Select(line => line.Text).ToList();
        return new OcrLineMergeResult(resultLines, lines.Count, resultLines.Count, events, mergedLines);
    }

    private static List<OcrLineInput> BuildInputs(
        IReadOnlyList<string> lines,
        IReadOnlyList<OcrTextLine>? textLines)
    {
        var hasMatchingTextLines = textLines is not null && textLines.Count == lines.Count;
        var inputs = lines
            .Select((line, index) => new OcrLineInput(
                index,
                hasMatchingTextLines ? textLines![index].RawIndex : index,
                hasMatchingTextLines && textLines![index].VisualOrder >= 0 ? textLines![index].VisualOrder : index,
                line,
                hasMatchingTextLines ? textLines![index].BoundingBox : null))
            .ToList();

        if (!inputs.Any(input => input.Box.HasValue))
        {
            return inputs;
        }

        return inputs
            .OrderBy(input => input.VisualOrder)
            .ThenBy(input => input.Box?.Top ?? double.MaxValue)
            .ThenBy(input => input.Box?.Left ?? double.MaxValue)
            .ThenBy(input => input.OriginalIndex)
            .ToList();
    }

    private static double? CalculateObservedRightEdge(IEnumerable<OcrLineInput> inputs)
    {
        return inputs
            .Where(input => input.Box.HasValue)
            .Select(input => input.Box!.Value.Right)
            .DefaultIfEmpty(double.NaN)
            .Max() is var rightEdge && double.IsNaN(rightEdge)
                ? null
                : rightEdge;
    }

    private static bool IsContinuation(
        string text,
        OcrLineInput current,
        MergedLine previous)
    {
        if (IsFullPlayerChatLine(text)
            || IsChannelPrefixLine(text)
            || IsContinuationBlocker(text)
            || ChatCleaner.IsInvalidMessage(text)
            || IsStandaloneShortMessage(text))
        {
            return false;
        }

        if (!IsLikelyWrappedMessage(previous.Text, text))
        {
            return false;
        }

        return IsNearPreviousLine(previous.LastInput, current);
    }

    private static bool IsFullPlayerChatLine(string text)
    {
        return PlayerChatLineRegex.IsMatch(text);
    }

    private static bool IsChannelPrefixLine(string text)
    {
        return ChannelPrefixRegex.IsMatch(text);
    }

    private static bool IsContinuationBlocker(string text)
    {
        if (TimestampPrefixRegex.IsMatch(text)
            || FuzzyTimestampPrefixRegex.IsMatch(text)
            || TimestampChampionEventRegex.IsMatch(text)
            || ChatDeduper.IsSystemOrCommandLine(text))
        {
            return true;
        }

        var compact = NormalizeBlockerText(text);
        return ContinuationBlockerKeywords.Any(keyword =>
            compact.Contains(NormalizeBlockerText(keyword), StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLikelyWrappedMessage(string previousLine, string continuation)
    {
        var previousMessage = ExtractMessage(previousLine);
        if (previousMessage.Length >= 28)
        {
            return true;
        }

        if (CountChinese(previousMessage) >= 8 || CountChinese(continuation) >= 6)
        {
            return true;
        }

        if (continuation.Length >= 18)
        {
            return true;
        }

        return previousMessage.EndsWith(",", StringComparison.Ordinal)
            || previousMessage.EndsWith("，", StringComparison.Ordinal)
            || previousMessage.EndsWith(" and", StringComparison.OrdinalIgnoreCase)
            || previousMessage.EndsWith(" but", StringComparison.OrdinalIgnoreCase)
            || previousMessage.EndsWith(" because", StringComparison.OrdinalIgnoreCase)
            || previousMessage.EndsWith(" so", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNearPreviousLine(OcrLineInput previous, OcrLineInput current)
    {
        if (previous.Box is not { } previousBox || current.Box is not { } currentBox)
        {
            var previousMessage = ExtractMessage(previous.Text);
            return previousMessage.Length >= 42
                && current.Text.Length >= 20
                && StartsLikeWrappedContinuation(current.Text);
        }

        var yDistance = currentBox.Top - previousBox.Top;
        var lineHeight = Math.Max(Math.Max(previousBox.Height, currentBox.Height), 12);
        return yDistance > lineHeight * 0.45
            && yDistance <= Math.Max(48, lineHeight * 2.4);
    }

    private static bool StartsLikeWrappedContinuation(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.Length > 0
            && (char.IsLower(trimmed[0])
                || IsChinese(trimmed[0])
                || trimmed[0] is ',' or '.' or '?' or '!' or ';' or ':' or '，' or '。' or '？' or '！' or '；' or '：');
    }

    private static bool IsStandaloneShortMessage(string text)
    {
        var normalized = MultiSpaceRegex.Replace(text.Trim().ToLowerInvariant(), " ");
        return normalized.Length <= 18 && StandaloneShortMessages.Contains(normalized);
    }

    private static string JoinContinuation(
        string previousLine,
        string continuation,
        OcrLineInput previousInput,
        OcrLineInput currentInput,
        double? observedRightEdge,
        out SplitJoinInfo? splitJoin)
    {
        splitJoin = null;
        var left = previousLine.TrimEnd();
        var right = continuation.TrimStart();
        if (string.IsNullOrWhiteSpace(left))
        {
            return right;
        }

        if (string.IsNullOrWhiteSpace(right))
        {
            return left;
        }

        if (ShouldJoinWithoutSpace(left, right, previousInput, observedRightEdge, out splitJoin))
        {
            return left + right;
        }

        return left + " " + right;
    }

    private static bool ShouldJoinWithoutSpace(
        string left,
        string right,
        OcrLineInput previousInput,
        double? observedRightEdge,
        out SplitJoinInfo? splitJoin)
    {
        splitJoin = null;
        var last = left[^1];
        var first = right[0];
        if (IsLeadingPunctuation(first)
            || IsChinese(last) && IsChinese(first)
            || last is '-' or '/' or '(' or '（')
        {
            return true;
        }

        if (char.IsAsciiLetter(last) && char.IsAsciiLetter(first))
        {
            var leftToken = GetTrailingAsciiWord(left);
            var rightToken = GetLeadingAsciiWord(right);
            var wasNearRightEdge = IsNearObservedRightEdge(previousInput, observedRightEdge);
            if (ShouldJoinSplitWord(leftToken, rightToken, wasNearRightEdge))
            {
                splitJoin = new SplitJoinInfo(leftToken, rightToken, leftToken + rightToken, wasNearRightEdge);
                return true;
            }
        }

        return false;
    }

    private static bool ShouldJoinSplitWord(string leftToken, string rightToken, bool wasNearRightEdge)
    {
        var combined = leftToken + rightToken;
        if (wasNearRightEdge
            && combined.Equals("and", StringComparison.OrdinalIgnoreCase)
            && leftToken.Length >= 2
            && rightToken.Length == 1)
        {
            return true;
        }

        if (leftToken.Length is < 2 or > 5
            || rightToken.Length is < 2 or > 5
            || CommonIndependentWords.Contains(leftToken)
            || CommonIndependentWords.Contains(rightToken))
        {
            return false;
        }

        if (KnownSplitWords.Contains(combined))
        {
            return true;
        }

        return wasNearRightEdge && combined.Length >= 5;
    }

    private static bool IsNearObservedRightEdge(OcrLineInput previousInput, double? observedRightEdge)
    {
        if (previousInput.Box is not { } box || !observedRightEdge.HasValue)
        {
            return false;
        }

        var tolerance = Math.Max(12, box.Height);
        return observedRightEdge.Value - box.Right <= tolerance;
    }

    private static string ExtractMessage(string line)
    {
        var match = PlayerChatLineRegex.Match(line);
        return match.Success
            ? NormalizeLine(match.Groups["message"].Value)
            : NormalizeLine(line);
    }

    private static string GetTrailingAsciiWord(string value)
    {
        var index = value.Length - 1;
        while (index >= 0 && char.IsAsciiLetter(value[index]))
        {
            index--;
        }

        return value[(index + 1)..];
    }

    private static string GetLeadingAsciiWord(string value)
    {
        var index = 0;
        while (index < value.Length && char.IsAsciiLetter(value[index]))
        {
            index++;
        }

        return value[..index];
    }

    private static bool IsLeadingPunctuation(char value)
    {
        return value is ',' or '.' or '?' or '!' or ';' or ':' or '，' or '。' or '？' or '！' or '；' or '：';
    }

    private static bool IsChinese(char value)
    {
        return value is >= '\u4e00' and <= '\u9fff';
    }

    private static int CountChinese(string value)
    {
        return value.Count(IsChinese);
    }

    private static string NormalizeLine(string value)
    {
        return MultiSpaceRegex.Replace(value.Normalize(NormalizationForm.FormKC).Trim(), " ");
    }

    private static string NormalizeBlockerText(string value)
    {
        return MultiSpaceRegex.Replace(value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant(), string.Empty);
    }

    private sealed record OcrLineInput(
        int OriginalIndex,
        int RawIndex,
        int VisualOrder,
        string Text,
        System.Windows.Rect? Box);

    private sealed class MergedLine
    {
        public MergedLine(string text, OcrLineInput input)
        {
            Text = text;
            FirstInput = input;
            LastInput = input;
            SourceRawIndices.Add(input.RawIndex);
            BoundingBox = input.Box;
        }

        public string Text { get; set; }

        public OcrLineInput FirstInput { get; }

        public OcrLineInput LastInput { get; set; }

        public List<int> SourceRawIndices { get; } = [];

        public System.Windows.Rect? BoundingBox { get; private set; }

        public MergedLine WithContinuation(string joinedText, OcrLineInput input)
        {
            Text = joinedText;
            LastInput = input;
            SourceRawIndices.Add(input.RawIndex);
            BoundingBox = Union(BoundingBox, input.Box);
            return this;
        }

        public MergedOcrLine ToMergedOcrLine()
        {
            return new MergedOcrLine(
                Text,
                FirstInput.VisualOrder,
                BoundingBox?.Top,
                BoundingBox?.Left,
                BoundingBox,
                SourceRawIndices.Distinct().OrderBy(index => index).ToList());
        }

        private static System.Windows.Rect? Union(System.Windows.Rect? first, System.Windows.Rect? second)
        {
            if (first is null)
            {
                return second;
            }

            if (second is null)
            {
                return first;
            }

            var rect = first.Value;
            rect.Union(second.Value);
            return rect;
        }
    }

    private sealed record SplitJoinInfo(
        string LeftToken,
        string RightToken,
        string JoinedToken,
        bool WasNearRightEdge);
}

public sealed record OcrLineMergeResult(
    List<string> Lines,
    int RawLineCount,
    int MergedLineCount,
    List<OcrLineMergeEvent> Events,
    List<MergedOcrLine> MergedLines);

public sealed record MergedOcrLine(
    string Text,
    int VisualOrder,
    double? Top,
    double? Left,
    System.Windows.Rect? BoundingBox,
    IReadOnlyList<int> SourceRawIndices);

public sealed record OcrLineMergeEvent(
    int ContinuationLineIndex,
    string BeforeText,
    string ContinuationText,
    string AfterText,
    string? SplitLeftToken = null,
    string? SplitRightToken = null,
    string? SplitJoinedToken = null,
    bool SplitWasNearRightEdge = false);
