using System.Text.RegularExpressions;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public sealed class PendingMessageStabilizer
{
    private const int LongMessageLengthThreshold = 35;
    private const int MinHoldMilliseconds = 250;
    private const int MinimumLongMessageHoldMilliseconds = 1200;
    private const int DefaultCaptureIntervalMilliseconds = 1000;

    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex SuspiciousGlueRegex = new(
        @"\b(?:ifrom(?:china|us|usa)|buti(?:not)?(?:like)?[a-z]*|i(?:also|alsi|alsom|alson)like[a-z]*|ilike[a-z]*|iverylike[a-z]*|doyoulike[a-z]*|appinc|chinesek[a-z]*|ongfu)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly List<PendingMessage> _pendingMessages = [];

    public bool HasPending => _pendingMessages.Count > 0;

    public void Clear()
    {
        _pendingMessages.Clear();
    }

    public List<CleanedChatMessage> GetStableMessages(
        IReadOnlyCollection<CleanedChatMessage> messages,
        bool forceFlushPending,
        bool allowImmediateLongMessages,
        string source,
        int captureIntervalMs = DefaultCaptureIntervalMilliseconds)
    {
        var now = DateTime.UtcNow;
        var holdMilliseconds = ResolveHoldMilliseconds(captureIntervalMs);
        var readyMessages = new List<CleanedChatMessage>();

        foreach (var message in messages)
        {
            if (!ShouldStabilize(message) || allowImmediateLongMessages)
            {
                readyMessages.Add(CloneMessage(message));
                continue;
            }

            var existing = _pendingMessages.FirstOrDefault(pending => IsSamePendingMessage(pending.Message, message));
            if (existing is null)
            {
                _pendingMessages.Add(new PendingMessage(CloneMessage(message), now, BuildCompletenessScore(message)));
                WritePendingLog("created", message, source, "long_or_unstable");
                continue;
            }

            var candidateScore = BuildCompletenessScore(message);
            var updated = false;
            if (IsBetterCandidate(message, candidateScore, existing.Message, existing.CompletenessScore))
            {
                existing.Message = CloneMessage(message);
                existing.CompletenessScore = candidateScore;
                existing.StableObservationCount = 0;
                existing.LastChangedUtc = now;
                updated = true;
                WritePendingLog("updated", message, source, "more_complete_or_better_normalized");
            }
            else if (AreEquivalentText(existing.Message.Message, message.Message))
            {
                existing.StableObservationCount++;
            }

            existing.ObservationCount++;
            existing.LastSeenUtc = now;

            if (CanRelease(existing, now, forceFlushPending, allowImmediateLongMessages, updated, holdMilliseconds))
            {
                readyMessages.Add(CloneMessage(existing.Message));
                _pendingMessages.Remove(existing);
                WritePendingLog("released", existing.Message, source, BuildReleaseReason(existing, now, holdMilliseconds));
            }
        }

        foreach (var stale in _pendingMessages
                     .Where(pending => CanReleaseStale(pending, now, forceFlushPending, allowImmediateLongMessages, holdMilliseconds))
                     .ToList())
        {
            readyMessages.Add(CloneMessage(stale.Message));
            _pendingMessages.Remove(stale);
            WritePendingLog("released", stale.Message, source, BuildReleaseReason(stale, now, holdMilliseconds));
        }

        return readyMessages
            .OrderBy(message => message.SourceOrder)
            .ThenBy(message => message.SourceTop ?? double.MaxValue)
            .ThenBy(message => message.SourceLeft ?? double.MaxValue)
            .ThenBy(message => message.SourceRawLineIndex)
            .ToList();
    }

    private static bool ShouldStabilize(CleanedChatMessage message)
    {
        var normalized = TranslationInputNormalizer.NormalizeForTranslation(message.Message);
        return normalized.Length >= LongMessageLengthThreshold
            || OcrEnglishGlueFixer.CountSuspiciousGlueMarkers(message.RawMessageBody) > 0
            || SuspiciousGlueRegex.IsMatch(message.Message);
    }

    private static bool CanRelease(
        PendingMessage pending,
        DateTime now,
        bool forceFlushPending,
        bool allowImmediateLongMessages,
        bool updatedThisCycle,
        int holdMilliseconds)
    {
        if (forceFlushPending && allowImmediateLongMessages)
        {
            return true;
        }

        var age = now - pending.FirstSeenUtc;
        if (age >= TimeSpan.FromMilliseconds(holdMilliseconds))
        {
            return true;
        }

        if (updatedThisCycle)
        {
            return false;
        }

        return pending.StableObservationCount >= 1
            && age >= TimeSpan.FromMilliseconds(MinHoldMilliseconds);
    }

    private static bool CanReleaseStale(
        PendingMessage pending,
        DateTime now,
        bool forceFlushPending,
        bool allowImmediateLongMessages,
        int holdMilliseconds)
    {
        return forceFlushPending && allowImmediateLongMessages
            || now - pending.FirstSeenUtc >= TimeSpan.FromMilliseconds(holdMilliseconds);
    }

    private static string BuildReleaseReason(PendingMessage pending, DateTime now, int holdMilliseconds)
    {
        var ageMs = (int)Math.Max(0, (now - pending.FirstSeenUtc).TotalMilliseconds);
        return pending.StableObservationCount >= 1
            ? $"stable_once age_ms={ageMs} hold_ms={holdMilliseconds}"
            : $"max_wait age_ms={ageMs} hold_ms={holdMilliseconds}";
    }

    private static int ResolveHoldMilliseconds(int captureIntervalMs)
    {
        var interval = Math.Clamp(captureIntervalMs, 250, 5000);
        return Math.Max(MinimumLongMessageHoldMilliseconds, interval + 300);
    }

    private static bool IsSamePendingMessage(CleanedChatMessage left, CleanedChatMessage right)
    {
        if (!BuildPendingKey(left).Equals(BuildPendingKey(right), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return AreLikelySameMessage(left.Message, right.Message);
    }

    private static bool AreLikelySameMessage(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        if (AreEquivalentText(left, right))
        {
            return true;
        }

        var leftCompact = NormalizeCompact(left);
        var rightCompact = NormalizeCompact(right);
        if (leftCompact.Length < 12 || rightCompact.Length < 12)
        {
            return false;
        }

        return IsStrictPrefix(leftCompact, rightCompact)
            || IsStrictPrefix(rightCompact, leftCompact)
            || TextSimilarity.NormalizedSimilarity(leftCompact, rightCompact) >= 0.86;
    }

    private static bool AreEquivalentText(string left, string right)
    {
        return NormalizeCompact(left).Equals(NormalizeCompact(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBetterCandidate(
        CleanedChatMessage candidate,
        int candidateScore,
        CleanedChatMessage current,
        int currentScore)
    {
        if (candidateScore > currentScore + 2)
        {
            return true;
        }

        var candidateCompact = NormalizeCompact(candidate.Message);
        var currentCompact = NormalizeCompact(current.Message);
        return IsStrictPrefix(currentCompact, candidateCompact);
    }

    private static bool IsStrictPrefix(string possiblePrefix, string fullText)
    {
        return possiblePrefix.Length >= 12
            && fullText.Length - possiblePrefix.Length >= 4
            && fullText.StartsWith(possiblePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static int BuildCompletenessScore(CleanedChatMessage message)
    {
        var normalized = TranslationInputNormalizer.NormalizeForTranslation(message.Message);
        var compactLength = NormalizeCompact(normalized).Length;
        var gluePenalty = (SuspiciousGlueRegex.Matches(message.Message).Count
            + OcrEnglishGlueFixer.CountSuspiciousGlueMarkers(message.RawMessageBody)) * 8;
        return compactLength - gluePenalty;
    }

    private static string BuildPendingKey(CleanedChatMessage message)
    {
        var channel = NormalizeKeyPart(message.RawChannelText ?? message.Channel.ToString());
        var player = NormalizeKeyPart(message.FixedPlayerName ?? message.OcrPlayerName);
        var timestamp = NormalizeKeyPart((message.Timestamp ?? string.Empty).Replace('：', ':'));
        var champion = NormalizeKeyPart(message.FixedChampionName ?? message.OcrChampionText);
        return $"{channel}|{player}|{timestamp}|{champion}";
    }

    private static string NormalizeCompact(string value)
    {
        var normalized = TranslationInputNormalizer.NormalizeForTranslation(value);
        var chars = normalized
            .Normalize()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray();
        return new string(chars);
    }

    private static string NormalizeKeyPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value
            .Normalize()
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars);
    }

    private static CleanedChatMessage CloneMessage(CleanedChatMessage message)
    {
        return new CleanedChatMessage
        {
            RawLine = message.RawLine,
            Timestamp = message.Timestamp,
            Channel = message.Channel,
            RawChannelText = message.RawChannelText,
            OcrPlayerName = message.OcrPlayerName,
            OcrChampionText = message.OcrChampionText,
            FixedPlayerName = message.FixedPlayerName,
            FixedChampionName = message.FixedChampionName,
            RawMessageBody = message.RawMessageBody,
            Message = message.Message,
            SourceOrder = message.SourceOrder,
            SourceTop = message.SourceTop,
            SourceLeft = message.SourceLeft,
            SourceRawLineIndex = message.SourceRawLineIndex
        };
    }

    private static void WritePendingLog(string action, CleanedChatMessage message, string source, string reason)
    {
        try
        {
            var normalized = TranslationInputNormalizer.NormalizeForTranslation(message.Message);
            var qualityScore = BuildCompletenessScore(message);
            var line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [PendingMessageStabilizer] " +
                $"action={action} " +
                $"key=\"{CleanLog(BuildPendingKey(message))}\" " +
                $"source=\"{CleanLog(source)}\" " +
                $"raw_text=\"{CleanLog(message.Message)}\" " +
                $"normalized_text=\"{CleanLog(normalized)}\" " +
                $"reason=\"{CleanLog(reason)}\"";
            AppLogService.AppendVerboseText(
                "pending-message-stabilizer.log",
                $"{line}{Environment.NewLine}" +
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [Pending][Decision] action={action} reason=\"{CleanLog(reason)}\" key=\"{CleanLog(BuildPendingKey(message))}\"{Environment.NewLine}" +
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [Pending][QualityScore] {qualityScore} raw_text=\"{CleanLog(message.RawMessageBody)}\" fixed_text=\"{CleanLog(message.Message)}\"{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never affect OCR or translation.
        }
    }

    private static string CleanLog(string value)
    {
        var text = MultiSpaceRegex.Replace(value.Replace('\r', ' ').Replace('\n', ' ').Trim(), " ");
        return text.Length <= 180 ? text : $"{text[..180]}...";
    }

    private sealed class PendingMessage
    {
        public PendingMessage(CleanedChatMessage message, DateTime now, int completenessScore)
        {
            Message = message;
            FirstSeenUtc = now;
            LastSeenUtc = now;
            LastChangedUtc = now;
            CompletenessScore = completenessScore;
        }

        public CleanedChatMessage Message { get; set; }

        public DateTime FirstSeenUtc { get; }

        public DateTime LastSeenUtc { get; set; }

        public DateTime LastChangedUtc { get; set; }

        public int ObservationCount { get; set; } = 1;

        public int StableObservationCount { get; set; }

        public int CompletenessScore { get; set; }
    }
}
