using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public sealed record PlayerExclusionDecision(
    bool Excluded,
    string Reason,
    string? MatchedRiotId,
    string? SenderName,
    string? SenderTag,
    string? ParsedRiotId,
    string MessageHash,
    string SanitizedRawOcrLine);

public static partial class PlayerExclusionService
{
    public const int MaxExcludedPlayers = 50;

    public static string NormalizePlayerName(string? name)
    {
        return NormalizeSpaces((name ?? string.Empty).Normalize(NormalizationForm.FormKC))
            .Trim()
            .ToLower(CultureInfo.InvariantCulture);
    }

    public static string NormalizePlayerTag(string? tag)
    {
        var normalized = NormalizeSpaces((tag ?? string.Empty).Normalize(NormalizationForm.FormKC))
            .Trim()
            .TrimStart('#', '＃')
            .Trim();

        return normalized.ToLower(CultureInfo.InvariantCulture);
    }

    public static string NormalizeRiotId(string? name, string? tag)
    {
        var normalizedName = NormalizePlayerName(name);
        var normalizedTag = NormalizePlayerTag(tag);
        return string.IsNullOrWhiteSpace(normalizedName) || string.IsNullOrWhiteSpace(normalizedTag)
            ? string.Empty
            : $"{normalizedName}#{normalizedTag}";
    }

    public static ExcludedPlayerEntry CreateEntry(string name, string tag, long? createdAt = null)
    {
        var displayName = NormalizeSpaces((name ?? string.Empty).Normalize(NormalizationForm.FormKC)).Trim();
        var displayTag = NormalizeSpaces((tag ?? string.Empty).Normalize(NormalizationForm.FormKC))
            .Trim()
            .TrimStart('#', '＃')
            .Trim();
        var normalizedName = NormalizePlayerName(displayName);
        var normalizedTag = NormalizePlayerTag(displayTag);

        return new ExcludedPlayerEntry
        {
            Name = displayName,
            Tag = displayTag,
            RiotId = $"{displayName}#{displayTag}",
            NormalizedName = normalizedName,
            NormalizedTag = normalizedTag,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }

    public static List<ExcludedPlayerEntry> NormalizeEntries(IEnumerable<ExcludedPlayerEntry>? entries)
    {
        var normalizedEntries = new List<ExcludedPlayerEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries ?? [])
        {
            var normalized = CreateEntry(
                FirstNonEmpty(entry.Name, ExtractName(entry.RiotId)),
                FirstNonEmpty(entry.Tag, ExtractTag(entry.RiotId)),
                entry.CreatedAt > 0 ? entry.CreatedAt : null);

            if (string.IsNullOrWhiteSpace(normalized.NormalizedName)
                || string.IsNullOrWhiteSpace(normalized.NormalizedTag))
            {
                continue;
            }

            var key = NormalizeRiotId(normalized.Name, normalized.Tag);
            if (!seen.Add(key))
            {
                continue;
            }

            normalizedEntries.Add(normalized);
            if (normalizedEntries.Count >= MaxExcludedPlayers)
            {
                break;
            }
        }

        return normalizedEntries;
    }

    public static PlayerExclusionDecision IsPlayerExcluded(
        CleanedChatMessage message,
        TranslateConfig settings)
    {
        var parsed = ChatDeduper.ParseChatLine(message.RawLine);
        var messageHash = HashMessage(message.Message);
        var sanitizedRawLine = BuildSanitizedRawLine(parsed, messageHash);

        if (!settings.ExcludePlayersEnabled)
        {
            return BuildDecision(false, "exclude_disabled", null, null, null, null, messageHash, sanitizedRawLine);
        }

        var entries = NormalizeEntries(settings.ExcludedPlayers);
        var sender = ResolveSender(message, parsed);
        if (sender is null || string.IsNullOrWhiteSpace(sender.Name))
        {
            return BuildDecision(false, "sender_missing", null, null, null, null, messageHash, sanitizedRawLine);
        }

        var parsedRiotId = string.IsNullOrWhiteSpace(sender.Tag)
            ? null
            : $"{sender.Name}#{sender.Tag}";

        if (!string.IsNullOrWhiteSpace(sender.Tag))
        {
            var exact = entries.FirstOrDefault(entry =>
                entry.NormalizedName.Equals(sender.NormalizedName, StringComparison.OrdinalIgnoreCase)
                && entry.NormalizedTag.Equals(sender.NormalizedTag, StringComparison.OrdinalIgnoreCase));

            return exact is null
                ? BuildDecision(false, "not_excluded", null, sender.Name, sender.Tag, parsedRiotId, messageHash, sanitizedRawLine)
                : BuildDecision(true, "excluded_player_exact_riot_id", exact.RiotId, sender.Name, sender.Tag, parsedRiotId, messageHash, sanitizedRawLine);
        }

        var sameNameEntries = entries
            .Where(entry => entry.NormalizedName.Equals(sender.NormalizedName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return sameNameEntries.Count switch
        {
            0 => BuildDecision(false, "not_excluded", null, sender.Name, null, null, messageHash, sanitizedRawLine),
            1 => BuildDecision(true, "excluded_player_unique_name_fallback", sameNameEntries[0].RiotId, sender.Name, null, null, messageHash, sanitizedRawLine),
            _ => BuildDecision(false, "ambiguous_name_without_tag", null, sender.Name, null, null, messageHash, sanitizedRawLine)
        };
    }

    public static void WriteDebugLog(PlayerExclusionDecision decision)
    {
        if (!decision.Excluded)
        {
            return;
        }

        try
        {
            AppLogService.AppendVerboseText(
                "player-exclusion.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {FormatDebugLog(decision)}{Environment.NewLine}");
        }
        catch
        {
            // Player exclusion logging must never affect OCR or translation.
        }
    }

    public static string FormatDebugLog(PlayerExclusionDecision decision)
    {
        return $"raw_ocr_line={CleanLog(decision.SanitizedRawOcrLine)} parsed_sender_name={decision.SenderName ?? "<none>"} parsed_sender_tag={decision.SenderTag ?? "<none>"} parsed_riot_id={decision.ParsedRiotId ?? "<none>"} excluded={decision.Excluded.ToString().ToLowerInvariant()} reason={decision.Reason} matched_riot_id={decision.MatchedRiotId ?? "<none>"} message_hash={decision.MessageHash}";
    }

    private static PlayerExclusionDecision BuildDecision(
        bool excluded,
        string reason,
        string? matchedRiotId,
        string? senderName,
        string? senderTag,
        string? parsedRiotId,
        string messageHash,
        string sanitizedRawLine)
    {
        return new PlayerExclusionDecision(
            excluded,
            reason,
            matchedRiotId,
            senderName,
            senderTag,
            parsedRiotId,
            messageHash,
            sanitizedRawLine);
    }

    private static ParsedSender? ResolveSender(CleanedChatMessage message, ParsedChatMessage parsed)
    {
        var candidates = new[]
        {
            message.OcrPlayerName,
            parsed.Sender,
            message.FixedPlayerName
        };

        foreach (var candidate in candidates)
        {
            var parsedSender = ParseSender(candidate);
            if (parsedSender is not null && !string.IsNullOrWhiteSpace(parsedSender.Tag))
            {
                return parsedSender;
            }
        }

        return candidates
            .Select(ParseSender)
            .FirstOrDefault(sender => sender is not null);
    }

    private static ParsedSender? ParseSender(string? sender)
    {
        if (string.IsNullOrWhiteSpace(sender))
        {
            return null;
        }

        var normalized = NormalizeSpaces(sender.Normalize(NormalizationForm.FormKC)).Trim();
        var riotIdMatch = RiotIdRegex().Match(normalized);
        var name = riotIdMatch.Success
            ? riotIdMatch.Groups["name"].Value
            : normalized;
        var tag = riotIdMatch.Success
            ? riotIdMatch.Groups["tag"].Value
            : null;

        name = NormalizeSpaces(name).Trim();
        tag = string.IsNullOrWhiteSpace(tag) ? null : NormalizePlayerTag(tag);
        var normalizedName = NormalizePlayerName(name);
        var normalizedTag = NormalizePlayerTag(tag);

        return string.IsNullOrWhiteSpace(normalizedName)
            ? null
            : new ParsedSender(name, tag, normalizedName, normalizedTag);
    }

    private static string BuildSanitizedRawLine(ParsedChatMessage parsed, string messageHash)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(parsed.Timestamp))
        {
            parts.Add(parsed.Timestamp);
        }

        if (!string.IsNullOrWhiteSpace(parsed.Channel))
        {
            parts.Add($"[{parsed.Channel}]");
        }

        if (!string.IsNullOrWhiteSpace(parsed.Sender))
        {
            parts.Add(parsed.Sender);
        }

        parts.Add($"<message_hash:{messageHash}>");
        return string.Join(" ", parts);
    }

    private static string HashMessage(string message)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(message ?? string.Empty));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string NormalizeSpaces(string value)
    {
        return SpaceRegex().Replace(value, " ");
    }

    private static string CleanLog(string? value)
    {
        return SpaceRegex().Replace(value ?? string.Empty, " ").Trim();
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string ExtractName(string? riotId)
    {
        var parsed = ParseSender(riotId);
        return parsed?.Name ?? string.Empty;
    }

    private static string ExtractTag(string? riotId)
    {
        var parsed = ParseSender(riotId);
        return parsed?.Tag ?? string.Empty;
    }

    private sealed record ParsedSender(string Name, string? Tag, string NormalizedName, string NormalizedTag);

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();

    [GeneratedRegex(@"^(?<name>.+?)\s*[#＃]\s*(?<tag>[A-Za-z0-9]{1,16})$")]
    private static partial Regex RiotIdRegex();
}
