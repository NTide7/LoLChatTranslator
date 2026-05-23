using System.Text.RegularExpressions;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public sealed class ChatCleaner
{
    private readonly ChannelAliasService _channelAliasService;

    public ChatCleaner()
        : this(new ChannelAliasService())
    {
    }

    public ChatCleaner(ChannelAliasService channelAliasService)
    {
        _channelAliasService = channelAliasService;
    }

    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex TimeOnlyRegex = new(
        @"^\s*\d{1,2}\s*[:：]\s*\d{2}\s*$",
        RegexOptions.Compiled);

    private static readonly Regex PureDigitsRegex = new(
        @"^\d+$",
        RegexOptions.Compiled);

    private static readonly Regex PurePunctuationRegex = new(
        @"^[\p{P}\p{S}]+$",
        RegexOptions.Compiled);

    private static readonly Regex DigitsAndPunctuationRegex = new(
        @"^[\d\p{P}\p{S}]+$",
        RegexOptions.Compiled);

    private static readonly Regex OcrRoundFragmentRegex = new(
        @"^[oO0]+$",
        RegexOptions.Compiled);

    private static readonly Regex OnlyBracketContentRegex = new(
        @"^[\(（][^)）]{0,64}[\)）][\p{P}\p{S}]*$",
        RegexOptions.Compiled);

    private static readonly Regex RightBracketResidueRegex = new(
        @"[\)）]\s*[\p{P}\p{S}]*$",
        RegexOptions.Compiled);

    private static readonly Regex ChineseTextRegex = new(
        @"[\u4e00-\u9fff]",
        RegexOptions.Compiled);

    private static readonly Regex AsciiWordRegex = new(
        @"[A-Za-z]{2,}",
        RegexOptions.Compiled);

    private static readonly Regex MojibakeLikeCharRegex = new(
        @"[^\u0000-\u007F\u4E00-\u9FFF\s\p{P}\p{S}]",
        RegexOptions.Compiled);

    private static readonly Regex TrailingPunctuationRegex = new(
        @"[\p{P}\p{S}]+$",
        RegexOptions.Compiled);

    public CleanedChatMessage? CleanMessage(string line, AppConfig config)
    {
        return CleanMessage(line, config, [], null);
    }

    public CleanedChatMessage? CleanMessage(
        string line,
        AppConfig config,
        IReadOnlyList<CurrentPlayerInfo> currentPlayers,
        PlayerNameMatcher? playerNameMatcher)
    {
        return CleanMessage(line, config, currentPlayers, playerNameMatcher, null);
    }

    public CleanedChatMessage? CleanMessage(
        MergedOcrLine line,
        AppConfig config,
        IReadOnlyList<CurrentPlayerInfo> currentPlayers,
        PlayerNameMatcher? playerNameMatcher)
    {
        return CleanMessage(line.Text, config, currentPlayers, playerNameMatcher, line);
    }

    private CleanedChatMessage? CleanMessage(
        string line,
        AppConfig config,
        IReadOnlyList<CurrentPlayerInfo> currentPlayers,
        PlayerNameMatcher? playerNameMatcher,
        MergedOcrLine? sourceLine)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var parsedLine = ChatDeduper.ParseChatLine(line);
        var validation = ChatDeduper.IsValidPlayerChat(parsedLine, _channelAliasService);
        ChatDeduper.WritePipelineDebugLog(parsedLine, validation);
        if (!validation.Valid)
        {
            return TryCreateUnfilteredSystemMessage(line, parsedLine, config, out var systemMessage)
                ? ApplySourceMetadata(systemMessage, sourceLine)
                : null;
        }

        var messageText = BuildTranslationMessageText(parsedLine, config.FilterConfig);
        if (IsInvalidMessage(messageText))
        {
            return null;
        }

        var matchedPlayer = playerNameMatcher?.Match(parsedLine.Sender, parsedLine.Champion, currentPlayers);

        return new CleanedChatMessage
        {
            RawLine = line,
            Timestamp = parsedLine.Timestamp,
            Channel = validation.Channel ?? ChatChannel.Unknown,
            RawChannelText = parsedLine.Channel,
            OcrPlayerName = parsedLine.Sender,
            OcrChampionText = parsedLine.Champion,
            FixedPlayerName = matchedPlayer?.PlayerName,
            FixedChampionName = matchedPlayer is null ? null : GetChampionDisplayName(matchedPlayer),
            RawMessageBody = parsedLine.RawMessageBody,
            Message = messageText,
            SourceOrder = sourceLine?.VisualOrder ?? 0,
            SourceTop = sourceLine?.Top,
            SourceLeft = sourceLine?.Left,
            SourceRawLineIndex = sourceLine?.SourceRawIndices.FirstOrDefault() ?? 0
        };
    }

    public List<CleanedChatMessage> CleanMessages(IEnumerable<string> lines, AppConfig config)
    {
        return CleanMessages(lines, config, [], null);
    }

    public List<CleanedChatMessage> CleanMessages(
        IEnumerable<string> lines,
        AppConfig config,
        IReadOnlyList<CurrentPlayerInfo> currentPlayers,
        PlayerNameMatcher? playerNameMatcher)
    {
        var cleanedMessages = new List<CleanedChatMessage>();

        var index = 0;
        foreach (var line in lines)
        {
            var sourceLine = new MergedOcrLine(line, index, null, null, null, [index]);
            var cleaned = CleanMessage(line, config, currentPlayers, playerNameMatcher, sourceLine);
            if (cleaned is not null)
            {
                cleanedMessages.Add(cleaned);
            }

            index++;
        }

        return cleanedMessages;
    }

    public List<CleanedChatMessage> CleanMessages(
        IEnumerable<MergedOcrLine> lines,
        AppConfig config)
    {
        return CleanMessages(lines, config, [], null);
    }

    public List<CleanedChatMessage> CleanMessages(
        IEnumerable<MergedOcrLine> lines,
        AppConfig config,
        IReadOnlyList<CurrentPlayerInfo> currentPlayers,
        PlayerNameMatcher? playerNameMatcher)
    {
        var cleanedMessages = new List<CleanedChatMessage>();

        foreach (var line in lines)
        {
            var cleaned = CleanMessage(line, config, currentPlayers, playerNameMatcher);
            if (cleaned is not null)
            {
                cleanedMessages.Add(cleaned);
            }
        }

        return cleanedMessages
            .OrderBy(message => message.SourceOrder)
            .ThenBy(message => message.SourceTop ?? double.MaxValue)
            .ThenBy(message => message.SourceLeft ?? double.MaxValue)
            .ToList();
    }

    private static string? GetChampionDisplayName(CurrentPlayerInfo player)
    {
        var chineseAlias = player.ChampionAliases
            .FirstOrDefault(alias => alias.Any(ch => ch > 127));

        if (!string.IsNullOrWhiteSpace(chineseAlias))
        {
            return chineseAlias;
        }

        return string.IsNullOrWhiteSpace(player.ChampionName)
            ? null
            : player.ChampionName;
    }

    private static string NormalizeSpaces(string? value)
    {
        return MultiSpaceRegex.Replace(value ?? string.Empty, " ").Trim();
    }

    private static string BuildTranslationMessageText(ParsedChatMessage parsedLine, FilterConfig config)
    {
        var body = NormalizeSpaces(parsedLine.Message);
        if (config.RemoveUsername)
        {
            return body;
        }

        var sender = NormalizeSpaces(parsedLine.Sender);
        var champion = NormalizeSpaces(parsedLine.Champion);
        if (!string.IsNullOrWhiteSpace(sender) && !string.IsNullOrWhiteSpace(champion))
        {
            return $"{sender}（{champion}）: {body}";
        }

        return string.IsNullOrWhiteSpace(sender)
            ? body
            : $"{sender}: {body}";
    }

    private static bool TryCreateUnfilteredSystemMessage(
        string rawLine,
        ParsedChatMessage parsedLine,
        AppConfig config,
        out CleanedChatMessage? message)
    {
        message = null;
        var kind = ChatDeduper.ClassifySystemMessage(parsedLine);
        if (kind == ChatSystemMessageKind.None || ShouldFilterSystemMessage(kind, config.FilterConfig))
        {
            return false;
        }

        var text = NormalizeSpaces(string.IsNullOrWhiteSpace(parsedLine.Message)
            ? parsedLine.RawMessageBody
            : parsedLine.Message);
        if (IsInvalidMessage(text))
        {
            return false;
        }

        message = new CleanedChatMessage
        {
            RawLine = rawLine,
            Timestamp = parsedLine.Timestamp,
            Channel = ChatChannel.System,
            RawChannelText = "system",
            OcrPlayerName = "system",
            OcrChampionText = "system",
            RawMessageBody = parsedLine.RawMessageBody,
            Message = text
        };
        return true;
    }

    private static CleanedChatMessage? ApplySourceMetadata(CleanedChatMessage? message, MergedOcrLine? sourceLine)
    {
        if (message is null)
        {
            return null;
        }

        message.SourceOrder = sourceLine?.VisualOrder ?? 0;
        message.SourceTop = sourceLine?.Top;
        message.SourceLeft = sourceLine?.Left;
        message.SourceRawLineIndex = sourceLine?.SourceRawIndices.FirstOrDefault() ?? 0;
        return message;
    }

    private static bool ShouldFilterSystemMessage(ChatSystemMessageKind kind, FilterConfig config)
    {
        return kind switch
        {
            ChatSystemMessageKind.CommandHelp => true,
            ChatSystemMessageKind.Ping => config.FilterPingMessages,
            ChatSystemMessageKind.Kill => config.FilterKillMessages,
            ChatSystemMessageKind.Purchase => config.FilterPurchaseMessages,
            ChatSystemMessageKind.System => config.FilterSystemMessages,
            _ => true
        };
    }

    public static bool IsInvalidMessage(string? message)
    {
        var text = NormalizeSpaces(message ?? string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (TimeOnlyRegex.IsMatch(text))
        {
            return true;
        }

        var compact = MultiSpaceRegex.Replace(text, string.Empty);
        var compactWithoutTrailingPunctuation = TrimTrailingPunctuation(compact);

        if (string.IsNullOrWhiteSpace(compactWithoutTrailingPunctuation))
        {
            return true;
        }

        if (compactWithoutTrailingPunctuation.Length == 1
            && (char.IsLetter(compactWithoutTrailingPunctuation[0]) || ChineseTextRegex.IsMatch(compactWithoutTrailingPunctuation)))
        {
            return false;
        }

        if (PureDigitsRegex.IsMatch(compactWithoutTrailingPunctuation)
            || PurePunctuationRegex.IsMatch(compact)
            || DigitsAndPunctuationRegex.IsMatch(compact))
        {
            return true;
        }

        if (OcrRoundFragmentRegex.IsMatch(compactWithoutTrailingPunctuation))
        {
            return true;
        }

        if (OnlyBracketContentRegex.IsMatch(text))
        {
            return true;
        }

        if (RightBracketResidueRegex.IsMatch(text)
            && (MojibakeLikeCharRegex.IsMatch(text) || ChineseTextRegex.IsMatch(text) || !text.Contains(' ')))
        {
            return true;
        }

        if (MojibakeLikeCharRegex.IsMatch(text)
            && !ChineseTextRegex.IsMatch(text)
            && (!AsciiWordRegex.IsMatch(text) || compact.Length <= 8))
        {
            return true;
        }

        return false;
    }

    private static string TrimTrailingPunctuation(string value)
    {
        return TrailingPunctuationRegex.Replace(value, string.Empty);
    }

}
