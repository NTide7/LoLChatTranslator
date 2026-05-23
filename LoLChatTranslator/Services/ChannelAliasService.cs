using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public sealed class ChannelAliasService
{
    private const string AliasFileName = "chat_channel_aliases.json";
    private const double FuzzyMatchThreshold = 0.72;

    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex BracketRegex = new(@"[\[\]【】［］]", RegexOptions.Compiled);
    private static readonly Regex PunctuationRegex = new(@"[:：.。]", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Dictionary<ChatChannel, ChannelAliasEntry> _aliases;

    public ChannelAliasService(string? aliasFilePath = null)
    {
        AliasFilePath = aliasFilePath ?? GetDefaultAliasFilePath();
        _aliases = LoadAliases();
    }

    public string AliasFilePath { get; }

    public static string EnsureAliasFileExists()
    {
        var path = GetDefaultAliasFilePath();
        try
        {
            if (!File.Exists(path))
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, JsonSerializer.Serialize(CreateDefaultAliasPack(), JsonOptions), Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to create channel alias file: {ex.Message}");
        }

        return path;
    }

    public ChatChannel? MatchChannelAlias(string rawChannelText)
    {
        var normalized = NormalizeChannelText(rawChannelText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        foreach (var channel in OrderedChannels())
        {
            if (!_aliases.TryGetValue(channel, out var entry))
            {
                continue;
            }

            if (entry.NormalizedAliases.Contains(normalized))
            {
                return channel;
            }
        }

        foreach (var channel in OrderedChannels())
        {
            if (!_aliases.TryGetValue(channel, out var entry))
            {
                continue;
            }

            if (entry.NormalizedOcrAliases.Contains(normalized))
            {
                return channel;
            }
        }

        ChatChannel? bestChannel = null;
        var bestScore = 0.0;

        foreach (var channel in OrderedChannels())
        {
            if (!_aliases.TryGetValue(channel, out var entry))
            {
                continue;
            }

            foreach (var alias in entry.NormalizedAliases.Concat(entry.NormalizedOcrAliases))
            {
                var score = CalculateSimilarity(normalized, alias);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestChannel = channel;
                }
            }
        }

        return bestScore >= FuzzyMatchThreshold ? bestChannel : null;
    }

    public static string NormalizeChannelText(string rawChannelText)
    {
        if (string.IsNullOrWhiteSpace(rawChannelText))
        {
            return string.Empty;
        }

        var normalized = rawChannelText
            .Trim()
            .Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant();

        normalized = BracketRegex.Replace(normalized, string.Empty);
        normalized = PunctuationRegex.Replace(normalized, string.Empty);
        normalized = MultiSpaceRegex.Replace(normalized, " ").Trim();

        var compact = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact switch
        {
            "aii" or "ali" or "ail" or "a1l" or "a11" => "all",
            "tearn" or "tcam" => "team",
            _ => normalized
        };
    }

    private Dictionary<ChatChannel, ChannelAliasEntry> LoadAliases()
    {
        try
        {
            if (!File.Exists(AliasFilePath))
            {
                return BuildAliasEntries(CreateDefaultAliasPack());
            }

            var json = File.ReadAllText(AliasFilePath, Encoding.UTF8);
            var pack = JsonSerializer.Deserialize<Dictionary<string, ChannelAliasDefinition>>(json, JsonOptions);
            if (pack is null || pack.Count == 0)
            {
                Log("Channel alias file is empty; using built-in defaults.");
                return BuildAliasEntries(CreateDefaultAliasPack());
            }

            return BuildAliasEntries(pack);
        }
        catch (Exception ex)
        {
            Log($"Failed to load channel alias file; using built-in defaults. {ex.Message}");
            return BuildAliasEntries(CreateDefaultAliasPack());
        }
    }

    private static Dictionary<ChatChannel, ChannelAliasEntry> BuildAliasEntries(
        Dictionary<string, ChannelAliasDefinition> pack)
    {
        var entries = new Dictionary<ChatChannel, ChannelAliasEntry>();

        foreach (var (key, definition) in pack)
        {
            if (!Enum.TryParse<ChatChannel>(key, ignoreCase: true, out var channel))
            {
                continue;
            }

            entries[channel] = new ChannelAliasEntry(
                definition.DisplayName,
                definition.Aliases,
                definition.OcrAliases);
        }

        foreach (var (key, definition) in CreateDefaultAliasPack())
        {
            if (Enum.TryParse<ChatChannel>(key, ignoreCase: true, out var channel)
                && !entries.ContainsKey(channel))
            {
                entries[channel] = new ChannelAliasEntry(
                    definition.DisplayName,
                    definition.Aliases,
                    definition.OcrAliases);
            }
        }

        return entries;
    }

    private static Dictionary<string, ChannelAliasDefinition> CreateDefaultAliasPack()
    {
        return new Dictionary<string, ChannelAliasDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["Team"] = new()
            {
                DisplayName = "队伍",
                Aliases =
                [
                    "队伍",
                    "隊伍",
                    "队伍聊天",
                    "隊伍聊天",
                    "team",
                    "ally",
                    "allies",
                    "팀",
                    "チーム",
                    "đội",
                    "doi",
                    "equipo",
                    "équipe",
                    "equipe",
                    "gruppe",
                    "squadra",
                    "time",
                    "drużyna",
                    "druzyna",
                    "команда",
                    "komanda",
                    "takım",
                    "takim"
                ],
                OcrAliases =
                [
                    "vxfh",
                    "vdh",
                    "v伍",
                    "队五",
                    "队人伍",
                    "隊五",
                    "tearn",
                    "tcam"
                ]
            },
            ["All"] = new()
            {
                DisplayName = "所有人",
                Aliases =
                [
                    "所有人",
                    "全部",
                    "全体",
                    "全體",
                    "all",
                    "전체",
                    "全員",
                    "全员",
                    "tất cả",
                    "tat ca",
                    "todos",
                    "tous",
                    "alle",
                    "tutti",
                    "все",
                    "vse",
                    "herkes"
                ],
                OcrAliases =
                [
                    "aii",
                    "aII",
                    "alI",
                    "ail",
                    "a1l",
                    "a11"
                ]
            },
            ["Party"] = new()
            {
                DisplayName = "小队",
                Aliases =
                [
                    "小队",
                    "小隊",
                    "party",
                    "premade",
                    "group",
                    "grupo",
                    "groupe",
                    "gruppe",
                    "gruppo",
                    "파티",
                    "パーティー",
                    "パーティ",
                    "nhóm",
                    "nhom",
                    "ปาร์ตี้",
                    "группа",
                    "gruppa",
                    "parti"
                ],
                OcrAliases =
                [
                    "partv",
                    "parfy",
                    "pany",
                    "小除",
                    "小队"
                ]
            },
            ["System"] = new()
            {
                DisplayName = "系统",
                Aliases =
                [
                    "系统",
                    "系統",
                    "system",
                    "game",
                    "游戏",
                    "遊戲"
                ],
                OcrAliases =
                [
                    "系统提示",
                    "系統提示",
                    "gamesystem"
                ]
            }
        };
    }

    private static IEnumerable<ChatChannel> OrderedChannels()
    {
        yield return ChatChannel.Team;
        yield return ChatChannel.All;
        yield return ChatChannel.Party;
        yield return ChatChannel.System;
    }

    private static double CalculateSimilarity(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0;
        }

        if (left.Equals(right, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return TextSimilarity.NormalizedSimilarity(left, right);
    }

    private static string GetDefaultAliasFilePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "Resources", AliasFileName);
    }

    private static void Log(string message)
    {
        try
        {
            AppLogService.AppendText(
                "channel-aliases.log",
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // Alias loading must never interrupt OCR translation.
        }
    }

    private sealed class ChannelAliasDefinition
    {
        public string DisplayName { get; set; } = string.Empty;

        public List<string> Aliases { get; set; } = [];

        public List<string> OcrAliases { get; set; } = [];
    }

    private sealed class ChannelAliasEntry
    {
        public ChannelAliasEntry(string displayName, IEnumerable<string> aliases, IEnumerable<string> ocrAliases)
        {
            DisplayName = displayName;
            NormalizedAliases = aliases
                .Select(NormalizeChannelText)
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            NormalizedOcrAliases = ocrAliases
                .Select(NormalizeChannelText)
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public string DisplayName { get; }

        public HashSet<string> NormalizedAliases { get; }

        public HashSet<string> NormalizedOcrAliases { get; }
    }
}
