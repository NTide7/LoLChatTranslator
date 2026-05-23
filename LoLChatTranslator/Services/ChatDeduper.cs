using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public sealed class ChatDeduper
{
    private const int PartialDuplicateWindowSeconds = 8;
    private const int PartialDuplicateMinLength = 24;
    private const int PartialDuplicateMinLengthDifference = 8;

    private static readonly Regex PlayerChatLineRegex = new(
        @"^\s*(?:(?<timestamp>\d{1,3}\s*[:：]\s*\d{2})\s*)?[\[【［](?<channel>[^\]】］]+)[\]】］]\s*(?<sender>.+?)\s*[（(]\s*(?<champion>[^）)]+?)\s*[）)]\s*[:：]\s*(?<message>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ChannelPrefixRegex = new(
        @"^\s*(?:(?<timestamp>\d{1,3}\s*[:：]\s*\d{2})\s*)?[\[【［](?<channel>[^\]】］]+)[\]】］]\s*(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex PlsGankGlueRegex = new(@"\b(pls|plz|please)gank\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GankLaneGlueRegex = new(@"\bgank(top|mid|middle|bot|bottom|jg|jungle)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LanePlsGlueRegex = new(@"\b(top|mid|middle|bot|bottom|jg|jungle)(pls|plz|please)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TrailingMessagePunctuationRegex = new(
        @"[\.\?!。？！!~～]+$",
        RegexOptions.Compiled);
    private static readonly Regex GameEndsSoonRegex = new(
        @"(?:游戏将在\d*分钟内结束|遊戲將在\d*分鐘內結束|gamewillendin)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PositionSelectedRegex = new(
        @"(?:已经选择了位置|已选择了位置|已选择位置|选择了(?:上路|中路|下路|打野|辅助))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] SystemMessageMarkers =
    [
        "暂停功能已被激活",
        "使用/pause指令来暂停游戏",
        "使用/resume指令来继续游戏",
        "游戏系统提示",
        "你已临近训练模式自由的最大游戏时长",
        "你已临近训练模式",
        "已临近",
        "最大游戏时长",
        "训练模式自由的最大游戏时长",
        "游戏将在",
        "分钟内结束",
        "你已臨近訓練模式",
        "最大遊戲時長",
        "遊戲將在",
        "分鐘內結束",
        "practicetool",
        "maximumgametime",
        "maximumduration",
        "joinedtheroom",
        "lefttheroom",
        "reconnected"
    ];

    private static readonly string[] PurchaseMessageMarkers =
    [
        "购买了",
        "已购买",
        "purchased"
    ];

    private static readonly string[] KillMessageMarkers =
    [
        "已经击杀",
        "已击杀",
        "hasbeenslain",
        "hasslain"
    ];

    private static readonly string[] PingMessageMarkers =
    [
        "signals",
        "isontheway",
        "enemymissing",
        "onmyway",
        "正在请求协助"
    ];

    private readonly object _syncRoot = new();
    private readonly HashSet<string> _timestampKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _noTimestampKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _timestampMessageFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RecentTranslatedMessage> _recentTranslatedMessages = [];
    private readonly ChannelAliasService _channelAliasService;
    private readonly TimeSpan _noTimestampTtl;

    public ChatDeduper()
        : this(new ChannelAliasService(), noTimestampTtlSeconds: 300)
    {
    }

    public ChatDeduper(int noTimestampTtlSeconds)
        : this(new ChannelAliasService(), noTimestampTtlSeconds)
    {
    }

    public ChatDeduper(ChannelAliasService channelAliasService, int noTimestampTtlSeconds = 300)
    {
        _channelAliasService = channelAliasService;
        _noTimestampTtl = TimeSpan.FromSeconds(Math.Max(1, noTimestampTtlSeconds));
    }

    public static ParsedChatMessage ParseChatLine(string line)
    {
        var rawLine = line ?? string.Empty;
        var normalizedLine = rawLine.Normalize(NormalizationForm.FormKC).Trim();
        var fullMatch = PlayerChatLineRegex.Match(normalizedLine);
        if (fullMatch.Success)
        {
            var rawMessageBody = NormalizeSpaces(fullMatch.Groups["message"].Value);
            var message = OcrEnglishGlueFixer.FixMessageBody(rawMessageBody);
            return new ParsedChatMessage
            {
                RawOcrLine = rawLine,
                Timestamp = NormalizeTimestamp(fullMatch.Groups["timestamp"].Value),
                Channel = NormalizeNullable(fullMatch.Groups["channel"].Value),
                Sender = NormalizeNullable(fullMatch.Groups["sender"].Value),
                Champion = NormalizeNullable(fullMatch.Groups["champion"].Value),
                RawMessageBody = rawMessageBody,
                Message = message,
                NormalizedMessage = NormalizeMessage(message),
                HasChannelTag = true,
                MatchedPlayerChatPattern = true
            };
        }

        var prefixMatch = ChannelPrefixRegex.Match(normalizedLine);
        if (prefixMatch.Success)
        {
            var rest = NormalizeSpaces(prefixMatch.Groups["rest"].Value);
            var sender = ExtractSenderBeforeColon(rest);
            var rawMessageBody = ExtractMessageAfterColon(rest) ?? rest;
            var message = OcrEnglishGlueFixer.FixMessageBody(rawMessageBody);
            return new ParsedChatMessage
            {
                RawOcrLine = rawLine,
                Timestamp = NormalizeTimestamp(prefixMatch.Groups["timestamp"].Value),
                Channel = NormalizeNullable(prefixMatch.Groups["channel"].Value),
                Sender = sender,
                RawMessageBody = rawMessageBody,
                Message = message,
                NormalizedMessage = NormalizeMessage(message),
                HasChannelTag = true,
                MatchedPlayerChatPattern = false
            };
        }

        var fallbackMessage = NormalizeSpaces(normalizedLine);
        return new ParsedChatMessage
        {
            RawOcrLine = rawLine,
            RawMessageBody = fallbackMessage,
            Message = fallbackMessage,
            NormalizedMessage = NormalizeMessage(fallbackMessage),
            HasChannelTag = false,
            MatchedPlayerChatPattern = false
        };
    }

    public static ChatLineValidationResult IsValidPlayerChat(
        ParsedChatMessage parsedLine,
        ChannelAliasService channelAliasService)
    {
        var compactRaw = NormalizeForSystemCheck(parsedLine.RawOcrLine);

        if (!parsedLine.MatchedPlayerChatPattern
            && ClassifySystemMessage(compactRaw) is { } systemKind
            && systemKind != ChatSystemMessageKind.None)
        {
            return ChatLineValidationResult.Invalid(SystemMessageReason(systemKind));
        }

        if (!parsedLine.HasChannelTag || string.IsNullOrWhiteSpace(parsedLine.Channel))
        {
            return ChatLineValidationResult.Invalid("missing_channel");
        }

        if (IsUnknownChannel(parsedLine.Channel))
        {
            return ChatLineValidationResult.Invalid("unknown_chat");
        }

        var channel = channelAliasService.MatchChannelAlias(parsedLine.Channel);
        if (channel is null)
        {
            return ChatLineValidationResult.Invalid("unknown_chat");
        }

        if (!parsedLine.MatchedPlayerChatPattern)
        {
            if (string.IsNullOrWhiteSpace(parsedLine.Sender))
            {
                return ChatLineValidationResult.Invalid("missing_sender");
            }

            if (string.IsNullOrWhiteSpace(parsedLine.Message))
            {
                return ChatLineValidationResult.Invalid("missing_message");
            }

            return ChatLineValidationResult.Invalid("missing_player_chat_pattern");
        }

        if (string.IsNullOrWhiteSpace(parsedLine.Sender))
        {
            return ChatLineValidationResult.Invalid("missing_sender");
        }

        if (string.IsNullOrWhiteSpace(parsedLine.Champion))
        {
            return ChatLineValidationResult.Invalid("missing_player_chat_pattern");
        }

        if (string.IsNullOrWhiteSpace(parsedLine.Message))
        {
            return ChatLineValidationResult.Invalid("missing_message");
        }

        return ChatLineValidationResult.CreateValid(channel.Value);
    }

    public static bool IsSystemOrCommandLine(string line)
    {
        if (PlayerChatLineRegex.IsMatch(line ?? string.Empty))
        {
            return false;
        }

        return ClassifySystemMessage(ParseChatLine(line ?? string.Empty)) != ChatSystemMessageKind.None;
    }

    public static ChatSystemMessageKind ClassifySystemMessage(ParsedChatMessage parsedLine)
    {
        if (parsedLine.MatchedPlayerChatPattern)
        {
            return ChatSystemMessageKind.None;
        }

        return ClassifySystemMessage(NormalizeForSystemCheck(parsedLine.RawOcrLine));
    }

    public static string NormalizeMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var normalized = OcrTextFixer.NormalizeReadableSpacing(message)
            .Normalize(NormalizationForm.FormKC)
            .Replace('：', ':')
            .Replace('　', ' ')
            .Trim()
            .ToLowerInvariant();

        normalized = MultiSpaceRegex.Replace(normalized, " ");
        normalized = PlsGankGlueRegex.Replace(normalized, "$1 gank");
        normalized = GankLaneGlueRegex.Replace(normalized, "gank $1");
        normalized = LanePlsGlueRegex.Replace(normalized, "$1 $2");
        normalized = OcrTextFixer.ApplyBuiltInFixes(normalized);
        normalized = OcrTextFixer.NormalizeReadableSpacing(normalized);
        normalized = MultiSpaceRegex.Replace(normalized, " ");
        normalized = TrailingMessagePunctuationRegex.Replace(normalized, string.Empty).Trim();
        return normalized;
    }

    public ChatDedupeDecision Probe(ParsedChatMessage parsedMessage)
    {
        var validation = IsValidPlayerChat(parsedMessage, _channelAliasService);
        if (!validation.Valid)
        {
            var invalidDecision = BuildDecision(
                parsedMessage,
                parsedMessage.NormalizedMessage,
                string.Empty,
                false,
                validation.Reason);
            WritePipelineDebugLog(parsedMessage, validation, invalidDecision);
            return invalidDecision;
        }

        var now = DateTimeOffset.UtcNow;
        var timestamp = NormalizeNullable(parsedMessage.Timestamp);
        var sender = NormalizeNullable(parsedMessage.Sender) ?? string.Empty;
        var rawMessage = NormalizeSpaces(string.IsNullOrWhiteSpace(parsedMessage.RawMessageBody)
            ? parsedMessage.Message
            : parsedMessage.RawMessageBody);
        var normalizedMessage = NormalizeMessage(parsedMessage.Message);
        var key = BuildDedupeKey(timestamp, sender, normalizedMessage);
        var noTimestampKey = BuildDedupeKey(null, sender, normalizedMessage);
        var fingerprintKey = BuildPlayerMessageFingerprint(sender, normalizedMessage);
        ChatDedupeDecision decision;

        lock (_syncRoot)
        {
            PruneExpiredNoTimestampKeys(now);

            var isPartialReplacement = TryFindPartialReplacementCandidate(
                now,
                parsedMessage.Channel,
                sender,
                timestamp,
                rawMessage,
                normalizedMessage,
                out var recentPartialMessage);

            if (TryFindPartialDuplicate(
                    now,
                    parsedMessage.Channel,
                    sender,
                    timestamp,
                    rawMessage,
                    normalizedMessage,
                    out var recentFullMessage))
            {
                decision = BuildDecision(
                    parsedMessage,
                    normalizedMessage,
                    key,
                    false,
                    "partial_duplicate_recent_full",
                    recentFullMessage);
            }
            else if (!string.IsNullOrWhiteSpace(timestamp))
            {
                if (_timestampKeys.Contains(key))
                {
                    decision = BuildDecision(
                        parsedMessage,
                        normalizedMessage,
                        key,
                        false,
                        "duplicate_same_timestamp");
                }
                else if (_noTimestampKeys.TryGetValue(noTimestampKey, out var noTimestampSeen)
                    && now - noTimestampSeen < _noTimestampTtl)
                {
                    decision = BuildDecision(
                        parsedMessage,
                        normalizedMessage,
                        key,
                        false,
                        "duplicate_timestamp_variant_within_ttl");
                }
                else
                {
                    decision = BuildDecision(
                        parsedMessage,
                        normalizedMessage,
                        key,
                        true,
                        isPartialReplacement ? "partial_replacement_recent_short" : "new_timestamp_message",
                        isPartialReplacement ? recentPartialMessage : null);
                }
            }
            else if (!_noTimestampKeys.TryGetValue(key, out var firstSeen))
            {
                if (_timestampMessageFingerprints.TryGetValue(fingerprintKey, out var timestampSeen)
                    && now - timestampSeen < _noTimestampTtl)
                {
                    decision = BuildDecision(
                        parsedMessage,
                        normalizedMessage,
                        key,
                        false,
                        "duplicate_timestamp_variant_within_ttl");
                }
                else
                {
                    decision = BuildDecision(
                        parsedMessage,
                        normalizedMessage,
                        key,
                        true,
                        isPartialReplacement ? "partial_replacement_recent_short" : "new_no_timestamp",
                        isPartialReplacement ? recentPartialMessage : null);
                }
            }
            else if (now - firstSeen < _noTimestampTtl)
            {
                decision = BuildDecision(
                    parsedMessage,
                    normalizedMessage,
                    key,
                    false,
                    "duplicate_within_ttl");
            }
            else
            {
                decision = BuildDecision(
                    parsedMessage,
                    normalizedMessage,
                    key,
                    true,
                    "expired_ttl");
            }
        }

        WritePipelineDebugLog(parsedMessage, validation, decision);
        return decision;
    }

    public ChatDedupeDecision ShouldTranslate(ParsedChatMessage parsedMessage)
    {
        return Probe(parsedMessage);
    }

    public ChatDedupeDecision ShouldTranslate(CleanedChatMessage message)
    {
        return Probe(message);
    }

    public ChatDedupeDecision Probe(CleanedChatMessage message)
    {
        return Probe(BuildParsedFromCleanedMessage(message));
    }

    public void CommitSuccess(CleanedChatMessage message)
    {
        CommitSuccess(BuildParsedFromCleanedMessage(message));
    }

    public void CommitSuccess(ParsedChatMessage parsedMessage)
    {
        var validation = IsValidPlayerChat(parsedMessage, _channelAliasService);
        if (!validation.Valid)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var timestamp = NormalizeNullable(parsedMessage.Timestamp);
        var sender = NormalizeNullable(parsedMessage.Sender) ?? string.Empty;
        var rawMessage = NormalizeSpaces(string.IsNullOrWhiteSpace(parsedMessage.RawMessageBody)
            ? parsedMessage.Message
            : parsedMessage.RawMessageBody);
        var normalizedMessage = NormalizeMessage(parsedMessage.Message);
        var key = BuildDedupeKey(timestamp, sender, normalizedMessage);
        var fingerprintKey = BuildPlayerMessageFingerprint(sender, normalizedMessage);

        lock (_syncRoot)
        {
            PruneExpiredNoTimestampKeys(now);
            if (!string.IsNullOrWhiteSpace(timestamp))
            {
                _timestampKeys.Add(key);
                _timestampMessageFingerprints[fingerprintKey] = now;
            }
            else
            {
                _noTimestampKeys[key] = now;
            }

            RememberRecentMessage(now, parsedMessage.Channel, sender, timestamp, rawMessage, normalizedMessage);
        }
    }

    public static void CommitDuplicate(ChatDedupeDecision decision)
    {
        WriteDecisionEvent("commit_duplicate", decision);
    }

    public static void CommitFiltered(ChatDedupeDecision decision)
    {
        WriteDecisionEvent("commit_filtered", decision);
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _timestampKeys.Clear();
            _noTimestampKeys.Clear();
            _timestampMessageFingerprints.Clear();
            _recentTranslatedMessages.Clear();
        }
    }

    public static string BuildStableIdentityKey(CleanedChatMessage message)
    {
        var parsed = ParseChatLine(message.RawLine);
        var timestamp = NormalizeNullable(message.Timestamp) ?? parsed.Timestamp;
        var sender = NormalizeNullable(message.FixedPlayerName)
            ?? NormalizeNullable(message.OcrPlayerName)
            ?? parsed.Sender
            ?? string.Empty;
        var normalizedMessage = NormalizeMessage(message.Message);

        return BuildDedupeKey(timestamp, sender, normalizedMessage);
    }

    public static void WritePipelineDebugLog(
        ParsedChatMessage parsed,
        ChatLineValidationResult validation,
        ChatDedupeDecision? decision = null)
    {
        try
        {
            var shouldTranslateText = decision is null
                ? "<pending>"
                : decision.ShouldTranslate.ToString().ToLowerInvariant();
            var dedupeReason = decision?.Reason ?? "<pending>";
            var duplicateReference = string.IsNullOrWhiteSpace(decision?.DuplicateReferenceText)
                ? "<none>"
                : CleanLog(decision.DuplicateReferenceText);
            var line = $"{DateTime.Now:HH:mm:ss} [ChatPipeline] raw_ocr_line=\"{CleanLog(parsed.RawOcrLine)}\" parsed_timestamp={parsed.Timestamp ?? "None"} parsed_channel={parsed.Channel ?? "<none>"} parsed_sender={parsed.Sender ?? "<none>"} parsed_champion={parsed.Champion ?? "<none>"} raw_message_body=\"{CleanLog(parsed.RawMessageBody)}\" parsed_message=\"{CleanLog(parsed.Message)}\" is_valid_player_chat={validation.Valid.ToString().ToLowerInvariant()} invalid_reason={(validation.Valid ? "valid_player_chat" : validation.Reason)} normalized_message=\"{CleanLog(parsed.NormalizedMessage)}\" dedupe_key={CleanLog(decision?.DedupeKey) switch { "" => "<none>", var value => value }} should_translate={shouldTranslateText} dedupe_reason={dedupeReason} partial_duplicate_reference=\"{duplicateReference}\"";
            AppLogService.AppendVerboseText("chat-pipeline-debug.log", $"{line}{Environment.NewLine}");
        }
        catch
        {
            // Chat pipeline debug logging must never affect OCR or translation.
        }
    }

    public static void WriteSkippedValidChatLog(CleanedChatMessage message, string reason)
    {
        var parsed = BuildParsedFromCleanedMessage(message);
        var validation = ChatLineValidationResult.CreateValid(message.Channel);
        var decision = BuildDecision(parsed, parsed.NormalizedMessage, string.Empty, false, reason);
        WritePipelineDebugLog(parsed, validation, decision);
    }

    private static ParsedChatMessage BuildParsedFromCleanedMessage(CleanedChatMessage message)
    {
        var parsed = ParseChatLine(message.RawLine);
        parsed.Timestamp = NormalizeNullable(message.Timestamp) ?? parsed.Timestamp;
        parsed.Channel = message.RawChannelText ?? parsed.Channel;
        parsed.Sender = NormalizeNullable(message.FixedPlayerName)
            ?? NormalizeNullable(message.OcrPlayerName)
            ?? parsed.Sender;
        parsed.Champion = NormalizeNullable(message.FixedChampionName)
            ?? NormalizeNullable(message.OcrChampionText)
            ?? parsed.Champion;
        parsed.RawMessageBody = string.IsNullOrWhiteSpace(message.RawMessageBody)
            ? message.Message
            : message.RawMessageBody;
        parsed.Message = message.Message;
        parsed.NormalizedMessage = NormalizeMessage(message.Message);
        parsed.HasChannelTag = true;
        parsed.MatchedPlayerChatPattern = true;
        return parsed;
    }

    private static ChatDedupeDecision BuildDecision(
        ParsedChatMessage parsedMessage,
        string normalizedMessage,
        string dedupeKey,
        bool shouldTranslate,
        string reason,
        string? duplicateReferenceText = null)
    {
        return new ChatDedupeDecision
        {
            ShouldTranslate = shouldTranslate,
            Reason = reason,
            DedupeKey = dedupeKey,
            Timestamp = parsedMessage.Timestamp,
            Channel = parsedMessage.Channel,
            Sender = parsedMessage.Sender,
            Champion = parsedMessage.Champion,
            Message = parsedMessage.Message,
            RawOcrLine = parsedMessage.RawOcrLine,
            NormalizedMessage = normalizedMessage,
            DuplicateReferenceText = duplicateReferenceText
        };
    }

    private static void WriteDecisionEvent(string action, ChatDedupeDecision decision)
    {
        try
        {
            AppLogService.AppendVerboseText(
                "chat-pipeline-debug.log",
                $"{DateTime.Now:HH:mm:ss} [ChatDeduper][{action}] dedupe_key={CleanLog(decision.DedupeKey)} reason={CleanLog(decision.Reason)} normalized_message=\"{CleanLog(decision.NormalizedMessage)}\"{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics only.
        }
    }

    private static string BuildDedupeKey(string? timestamp, string sender, string normalizedMessage)
    {
        var senderKey = NormalizeSpaces(sender);

        return !string.IsNullOrWhiteSpace(timestamp)
            ? $"ts|{timestamp}|{senderKey}|{normalizedMessage}"
            : $"no_ts|{senderKey}|{normalizedMessage}";
    }

    private static string BuildPlayerMessageFingerprint(string sender, string normalizedMessage)
    {
        return $"msg|{NormalizeSpaces(sender)}|{normalizedMessage}";
    }

    private void PruneExpiredNoTimestampKeys(DateTimeOffset now)
    {
        foreach (var pair in _noTimestampKeys.ToList())
        {
            if (now - pair.Value >= _noTimestampTtl)
            {
                _noTimestampKeys.Remove(pair.Key);
            }
        }

        foreach (var pair in _timestampMessageFingerprints.ToList())
        {
            if (now - pair.Value >= _noTimestampTtl)
            {
                _timestampMessageFingerprints.Remove(pair.Key);
            }
        }

        _recentTranslatedMessages.RemoveAll(message =>
            now - message.SeenAt >= TimeSpan.FromSeconds(PartialDuplicateWindowSeconds));
    }

    private bool TryFindPartialDuplicate(
        DateTimeOffset now,
        string? channel,
        string sender,
        string? timestamp,
        string rawMessage,
        string normalizedMessage,
        out string recentFullMessage)
    {
        recentFullMessage = string.Empty;
        var currentRaw = NormalizePartialDuplicateRawText(rawMessage);
        var currentRawCompact = NormalizePartialDuplicateRawCompact(rawMessage);
        var current = NormalizePartialDuplicateText(normalizedMessage);
        var currentCompact = NormalizePartialDuplicateCompact(normalizedMessage);
        if (!CanTakePartInPartialDuplicateCheck(currentRaw)
            && !CanTakeCompactPartInPartialDuplicateCheck(currentRawCompact)
            && !CanTakePartInPartialDuplicateCheck(current)
            && !CanTakeCompactPartInPartialDuplicateCheck(currentCompact))
        {
            return false;
        }

        foreach (var recent in _recentTranslatedMessages.AsEnumerable().Reverse())
        {
            if (now - recent.SeenAt > TimeSpan.FromSeconds(PartialDuplicateWindowSeconds)
                || !IsSamePartialDuplicateScope(recent, channel, sender, timestamp))
            {
                continue;
            }

            var lengthDifference = 0;
            if (CanTakePartInPartialDuplicateCheck(currentRaw)
                && IsStrictTextPrefix(currentRaw, recent.RawPartialKey, out lengthDifference)
                && lengthDifference >= PartialDuplicateMinLengthDifference)
            {
                recentFullMessage = recent.NormalizedMessage;
                return true;
            }

            if (CanTakeCompactPartInPartialDuplicateCheck(currentRawCompact)
                && currentRawCompact.Equals(recent.RawCompactKey, StringComparison.OrdinalIgnoreCase))
            {
                recentFullMessage = recent.NormalizedMessage;
                return true;
            }

            if (CanTakeCompactPartInPartialDuplicateCheck(currentRawCompact)
                && IsLikelyCompactPartialDuplicate(currentRawCompact, recent.RawCompactKey, out lengthDifference)
                && lengthDifference >= PartialDuplicateMinLengthDifference)
            {
                recentFullMessage = recent.NormalizedMessage;
                return true;
            }

            if (CanTakePartInPartialDuplicateCheck(current)
                && IsStrictTextPrefix(current, recent.PartialKey, out lengthDifference)
                && lengthDifference >= PartialDuplicateMinLengthDifference)
            {
                recentFullMessage = recent.NormalizedMessage;
                return true;
            }

            if (CanTakeCompactPartInPartialDuplicateCheck(currentCompact)
                && currentCompact.Equals(recent.CompactKey, StringComparison.OrdinalIgnoreCase))
            {
                recentFullMessage = recent.NormalizedMessage;
                return true;
            }

            if (CanTakeCompactPartInPartialDuplicateCheck(currentCompact)
                && IsLikelyCompactPartialDuplicate(currentCompact, recent.CompactKey, out lengthDifference)
                && lengthDifference >= PartialDuplicateMinLengthDifference)
            {
                recentFullMessage = recent.NormalizedMessage;
                return true;
            }
        }

        return false;
    }

    private bool TryFindPartialReplacementCandidate(
        DateTimeOffset now,
        string? channel,
        string sender,
        string? timestamp,
        string rawMessage,
        string normalizedMessage,
        out string recentPartialMessage)
    {
        recentPartialMessage = string.Empty;
        var currentRaw = NormalizePartialDuplicateRawText(rawMessage);
        var currentRawCompact = NormalizePartialDuplicateRawCompact(rawMessage);
        var current = NormalizePartialDuplicateText(normalizedMessage);
        var currentCompact = NormalizePartialDuplicateCompact(normalizedMessage);
        if (!CanTakePartInPartialDuplicateCheck(currentRaw)
            && !CanTakeCompactPartInPartialDuplicateCheck(currentRawCompact)
            && !CanTakePartInPartialDuplicateCheck(current)
            && !CanTakeCompactPartInPartialDuplicateCheck(currentCompact))
        {
            return false;
        }

        foreach (var recent in _recentTranslatedMessages.AsEnumerable().Reverse())
        {
            if (now - recent.SeenAt > TimeSpan.FromSeconds(PartialDuplicateWindowSeconds)
                || !IsSamePartialDuplicateScope(recent, channel, sender, timestamp))
            {
                continue;
            }

            if (IsRecentShortPrefixOfCurrent(recent.RawPartialKey, currentRaw)
                || IsRecentShortPrefixOfCurrent(recent.RawCompactKey, currentRawCompact, compact: true)
                || IsRecentShortPrefixOfCurrent(recent.PartialKey, current)
                || IsRecentShortPrefixOfCurrent(recent.CompactKey, currentCompact, compact: true))
            {
                recentPartialMessage = recent.NormalizedMessage;
                return true;
            }
        }

        return false;
    }

    private void RememberRecentMessage(
        DateTimeOffset now,
        string? channel,
        string sender,
        string? timestamp,
        string rawMessage,
        string normalizedMessage)
    {
        var rawKey = NormalizePartialDuplicateRawText(rawMessage);
        var rawCompactKey = NormalizePartialDuplicateRawCompact(rawMessage);
        var key = NormalizePartialDuplicateText(normalizedMessage);
        var compactKey = NormalizePartialDuplicateCompact(normalizedMessage);
        if (!CanTakePartInPartialDuplicateCheck(rawKey)
            && !CanTakeCompactPartInPartialDuplicateCheck(rawCompactKey)
            && !CanTakePartInPartialDuplicateCheck(key)
            && !CanTakeCompactPartInPartialDuplicateCheck(compactKey))
        {
            return;
        }

        _recentTranslatedMessages.Add(new RecentTranslatedMessage(
            NormalizeNullable(channel) ?? string.Empty,
            NormalizeNullable(sender) ?? string.Empty,
            NormalizeTimestamp(timestamp ?? string.Empty) ?? string.Empty,
            normalizedMessage,
            rawKey,
            rawCompactKey,
            key,
            compactKey,
            now));
    }

    private static bool IsSamePartialDuplicateScope(
        RecentTranslatedMessage recent,
        string? channel,
        string sender,
        string? timestamp)
    {
        var normalizedSender = NormalizeNullable(sender) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedSender)
            || !recent.Sender.Equals(normalizedSender, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalizedChannel = NormalizeNullable(channel) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(recent.Channel)
            && !string.IsNullOrWhiteSpace(normalizedChannel)
            && !recent.Channel.Equals(normalizedChannel, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalizedTimestamp = NormalizeTimestamp(timestamp ?? string.Empty) ?? string.Empty;
        return string.IsNullOrWhiteSpace(recent.Timestamp)
            || string.IsNullOrWhiteSpace(normalizedTimestamp)
            || recent.Timestamp.Equals(normalizedTimestamp, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanTakePartInPartialDuplicateCheck(string value)
    {
        return value.Length >= PartialDuplicateMinLength
            && value.Count(char.IsWhiteSpace) >= 4;
    }

    private static bool CanTakeCompactPartInPartialDuplicateCheck(string value)
    {
        return value.Length >= PartialDuplicateMinLength;
    }

    private static bool IsStrictTextPrefix(string possiblePrefix, string fullText, out int lengthDifference)
    {
        lengthDifference = fullText.Length - possiblePrefix.Length;
        if (lengthDifference <= 0)
        {
            return false;
        }

        if (!fullText.StartsWith(possiblePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fullText.Length == possiblePrefix.Length
            || fullText[possiblePrefix.Length] == ' '
            || possiblePrefix.EndsWith(' ')
            || IsLikelySplitTokenPrefix(possiblePrefix, fullText);
    }

    private static bool IsStrictCompactTextPrefix(string possiblePrefix, string fullText, out int lengthDifference)
    {
        lengthDifference = fullText.Length - possiblePrefix.Length;
        return lengthDifference > 0
            && fullText.StartsWith(possiblePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyCompactPartialDuplicate(string possiblePrefix, string fullText, out int lengthDifference)
    {
        if (IsStrictCompactTextPrefix(possiblePrefix, fullText, out lengthDifference))
        {
            return true;
        }

        lengthDifference = fullText.Length - possiblePrefix.Length;
        if (lengthDifference <= 0)
        {
            return false;
        }

        var commonLength = CountCommonPrefix(possiblePrefix, fullText);
        var unmatchedTailLength = possiblePrefix.Length - commonLength;
        return commonLength >= PartialDuplicateMinLength
            && unmatchedTailLength is >= 1 and <= 4;
    }

    private static int CountCommonPrefix(string left, string right)
    {
        var max = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < max && char.ToLowerInvariant(left[index]) == char.ToLowerInvariant(right[index]))
        {
            index++;
        }

        return index;
    }

    private static bool IsLikelySplitTokenPrefix(string possiblePrefix, string fullText)
    {
        var nextIndex = possiblePrefix.Length;
        if (nextIndex >= fullText.Length || !char.IsAsciiLetter(fullText[nextIndex]))
        {
            return false;
        }

        var trailingToken = GetTrailingAsciiWord(possiblePrefix);
        if (trailingToken.Length is < 2 or > 5)
        {
            return false;
        }

        var tokenEnd = nextIndex;
        while (tokenEnd < fullText.Length && char.IsAsciiLetter(fullText[tokenEnd]))
        {
            tokenEnd++;
        }

        var completedTokenLength = trailingToken.Length + tokenEnd - nextIndex;
        return completedTokenLength <= 12;
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

    private static string NormalizePartialDuplicateText(string value)
    {
        return NormalizePartialDuplicateTokens(value, applyTranslationInputNormalizer: true);
    }

    private static string NormalizePartialDuplicateRawText(string value)
    {
        return NormalizePartialDuplicateTokens(value, applyTranslationInputNormalizer: false);
    }

    private static string NormalizePartialDuplicateRawCompact(string value)
    {
        return NormalizePartialDuplicateCompact(value, applyTranslationInputNormalizer: false);
    }

    private static string NormalizePartialDuplicateCompact(string value)
    {
        return NormalizePartialDuplicateCompact(value, applyTranslationInputNormalizer: true);
    }

    private static string NormalizePartialDuplicateCompact(string value, bool applyTranslationInputNormalizer)
    {
        var source = applyTranslationInputNormalizer
            ? TranslationInputNormalizer.NormalizeForTranslation(value)
            : value;
        var normalized = source
            .Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant();
        var chars = normalized
            .Where(char.IsLetterOrDigit)
            .ToArray();
        return new string(chars);
    }

    private static string NormalizePartialDuplicateTokens(string value, bool applyTranslationInputNormalizer)
    {
        var source = applyTranslationInputNormalizer
            ? TranslationInputNormalizer.NormalizeForTranslation(value)
            : value;
        var builder = new StringBuilder();
        var previousWasSpace = true;
        foreach (var ch in source.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousWasSpace = false;
                continue;
            }

            if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return MultiSpaceRegex.Replace(builder.ToString().Trim(), " ");
    }

    private static string? NormalizeTimestamp(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Normalize(NormalizationForm.FormKC).Replace('：', ':');
        text = MultiSpaceRegex.Replace(text, string.Empty);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string NormalizeSpaces(string value)
    {
        return MultiSpaceRegex.Replace(value.Trim(), " ");
    }

    private static string? NormalizeNullable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return NormalizeSpaces(value.Normalize(NormalizationForm.FormKC));
    }

    private static string? ExtractSenderBeforeColon(string rest)
    {
        var index = rest.IndexOfAny([':', '：']);
        if (index <= 0)
        {
            return null;
        }

        var prefix = rest[..index].Trim();
        var bracketIndex = prefix.IndexOfAny(['(', '（']);
        if (bracketIndex > 0)
        {
            prefix = prefix[..bracketIndex].Trim();
        }

        return NormalizeNullable(prefix);
    }

    private static string? ExtractMessageAfterColon(string rest)
    {
        var index = rest.IndexOfAny([':', '：']);
        if (index < 0 || index + 1 >= rest.Length)
        {
            return null;
        }

        return NormalizeNullable(rest[(index + 1)..]);
    }

    private static bool IsUnknownChannel(string channel)
    {
        var normalized = ChannelAliasService.NormalizeChannelText(channel);
        return normalized is "未知" or "unknown" or "unk" or "none" or "?";
    }

    private static bool IsCommandHelp(string compactRaw)
    {
        return compactRaw.Contains("输入/help获取命令列表", StringComparison.Ordinal)
            || compactRaw.Contains("/help", StringComparison.OrdinalIgnoreCase)
            && compactRaw.Contains("命令列表", StringComparison.Ordinal);
    }

    private static bool IsRecentShortPrefixOfCurrent(string recentShort, string currentFull, bool compact = false)
    {
        if (string.IsNullOrWhiteSpace(recentShort) || string.IsNullOrWhiteSpace(currentFull))
        {
            return false;
        }

        if (compact)
        {
            return IsLikelyCompactPartialDuplicate(recentShort, currentFull, out var lengthDifference)
                && lengthDifference >= PartialDuplicateMinLengthDifference;
        }

        return IsStrictTextPrefix(recentShort, currentFull, out var textLengthDifference)
            && textLengthDifference >= PartialDuplicateMinLengthDifference;
    }

    private static ChatSystemMessageKind ClassifySystemMessage(string compactRaw)
    {
        if (IsCommandHelp(compactRaw))
        {
            return ChatSystemMessageKind.CommandHelp;
        }

        if (PingMessageMarkers.Any(marker => compactRaw.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return ChatSystemMessageKind.Ping;
        }

        if (KillMessageMarkers.Any(marker => compactRaw.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return ChatSystemMessageKind.Kill;
        }

        if (PurchaseMessageMarkers.Any(marker => compactRaw.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return ChatSystemMessageKind.Purchase;
        }

        if (GameEndsSoonRegex.IsMatch(compactRaw)
            || PositionSelectedRegex.IsMatch(compactRaw)
            || SystemMessageMarkers.Any(marker => compactRaw.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return ChatSystemMessageKind.System;
        }

        return ChatSystemMessageKind.None;
    }

    private static string SystemMessageReason(ChatSystemMessageKind kind)
    {
        return kind switch
        {
            ChatSystemMessageKind.CommandHelp => "command_help",
            ChatSystemMessageKind.Ping => "ping_message",
            ChatSystemMessageKind.Kill => "kill_message",
            ChatSystemMessageKind.Purchase => "purchase_message",
            ChatSystemMessageKind.System => "system_message",
            _ => "valid_player_chat"
        };
    }

    private static string NormalizeForSystemCheck(string value)
    {
        var normalized = (value ?? string.Empty)
            .Normalize(NormalizationForm.FormKC)
            .Trim()
            .ToLowerInvariant();
        return MultiSpaceRegex.Replace(normalized, string.Empty);
    }

    private static string CleanLog(string? value)
    {
        return MultiSpaceRegex.Replace(value ?? string.Empty, " ").Trim();
    }
}

public sealed class ParsedChatMessage
{
    public string RawOcrLine { get; set; } = string.Empty;

    public string? Timestamp { get; set; }

    public string? Channel { get; set; }

    public string? Sender { get; set; }

    public string? Champion { get; set; }

    public string RawMessageBody { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string NormalizedMessage { get; set; } = string.Empty;

    public bool HasChannelTag { get; set; }

    public bool MatchedPlayerChatPattern { get; set; }
}

public sealed record ChatLineValidationResult(bool Valid, string Reason, ChatChannel? Channel)
{
    public static ChatLineValidationResult CreateValid(ChatChannel channel)
    {
        return new ChatLineValidationResult(true, "valid_player_chat", channel);
    }

    public static ChatLineValidationResult Invalid(string reason)
    {
        return new ChatLineValidationResult(false, reason, null);
    }
}

public enum ChatSystemMessageKind
{
    None,
    CommandHelp,
    System,
    Ping,
    Kill,
    Purchase
}

public sealed class ChatDedupeDecision
{
    public bool ShouldTranslate { get; init; }

    public string Reason { get; init; } = string.Empty;

    public string DedupeKey { get; init; } = string.Empty;

    public string? Timestamp { get; init; }

    public string? Channel { get; init; }

    public string? Sender { get; init; }

    public string? Champion { get; init; }

    public string Message { get; init; } = string.Empty;

    public string RawOcrLine { get; init; } = string.Empty;

    public string NormalizedMessage { get; init; } = string.Empty;

    public string? DuplicateReferenceText { get; init; }
}

internal sealed record RecentTranslatedMessage(
    string Channel,
    string Sender,
    string Timestamp,
    string NormalizedMessage,
    string RawPartialKey,
    string RawCompactKey,
    string PartialKey,
    string CompactKey,
    DateTimeOffset SeenAt);
