using System.Diagnostics;
using System.IO;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public sealed class ChampionAliasService
{
    private const string AliasFileName = "champion_aliases.txt";

    private readonly object _syncRoot = new();
    private Dictionary<string, ChampionAliasEntry>? _aliasesByKey;

    public List<string> GetAliases(string championName, string rawChampionName = "")
    {
        var entries = LoadAliases();
        var keys = BuildLookupKeys(championName, rawChampionName);

        foreach (var key in keys)
        {
            if (entries.TryGetValue(key, out var entry))
            {
                return entry.AllAliases
                    .Where(alias => !string.IsNullOrWhiteSpace(alias))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        return string.IsNullOrWhiteSpace(championName) ? [] : [championName];
    }

    public CurrentPlayerInfo EnrichPlayer(CurrentPlayerInfo player)
    {
        player.ChampionAliases = GetAliases(player.ChampionName, player.RawChampionName);
        return player;
    }

    private Dictionary<string, ChampionAliasEntry> LoadAliases()
    {
        lock (_syncRoot)
        {
            if (_aliasesByKey is not null)
            {
                return _aliasesByKey;
            }

            var loaded = new Dictionary<string, ChampionAliasEntry>(StringComparer.OrdinalIgnoreCase);
            var aliasPath = Path.Combine(AppContext.BaseDirectory, AliasFileName);

            if (!File.Exists(aliasPath))
            {
                _aliasesByKey = loaded;
                return loaded;
            }

            foreach (var line in File.ReadLines(aliasPath))
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                {
                    continue;
                }

                var parts = line.Split('\t');
                if (parts.Length < 4)
                {
                    Trace.TraceError($"Invalid champion alias line: {line}");
                    continue;
                }

                var entry = ChampionAliasEntry.FromParts(parts);
                foreach (var key in entry.LookupKeys)
                {
                    loaded[key] = entry;
                }
            }

            _aliasesByKey = loaded;
            return loaded;
        }
    }

    private static IEnumerable<string> BuildLookupKeys(string championName, string rawChampionName)
    {
        var values = new[]
        {
            championName,
            rawChampionName,
            ExtractChampionKey(rawChampionName)
        };

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string ExtractChampionKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lastUnderscore = value.LastIndexOf('_');
        return lastUnderscore >= 0 && lastUnderscore + 1 < value.Length
            ? value[(lastUnderscore + 1)..]
            : value;
    }

    private static string NormalizeKey(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private sealed class ChampionAliasEntry
    {
        public required List<string> AllAliases { get; init; }

        public required List<string> LookupKeys { get; init; }

        public static ChampionAliasEntry FromParts(string[] parts)
        {
            var key = parts[0].Trim();
            var en = parts[1].Trim();
            var zhName = parts[2].Trim();
            var zhTitle = parts[3].Trim();
            var aliases = parts.Length >= 5
                ? parts[4].Split(['|', ',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];

            var allAliases = new[] { key, en, zhName, zhTitle }
                .Concat(aliases)
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var lookupKeys = allAliases
                .Select(NormalizeKey)
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new ChampionAliasEntry
            {
                AllAliases = allAliases,
                LookupKeys = lookupKeys
            };
        }
    }
}
