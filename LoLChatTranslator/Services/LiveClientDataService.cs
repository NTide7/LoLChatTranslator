using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public sealed class LiveClientDataService
{
    private const string PlayerListUrl = "https://127.0.0.1:2999/liveclientdata/playerlist";
    private const string ActivePlayerUrl = "https://127.0.0.1:2999/liveclientdata/activeplayer";

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private readonly ChampionAliasService _championAliasService;

    public LiveClientDataService(ChampionAliasService championAliasService)
    {
        _championAliasService = championAliasService;
    }

    public async Task<List<CurrentPlayerInfo>> GetCurrentPlayersAsync()
    {
        try
        {
            var players = await HttpClient.GetFromJsonAsync<List<LiveClientPlayer>>(PlayerListUrl);
            if (players is null)
            {
                return [];
            }

            return players
                .Select(ToCurrentPlayerInfo)
                .Where(player => !string.IsNullOrWhiteSpace(player.PlayerName))
                .Select(_championAliasService.EnrichPlayer)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Trace.TraceError($"Live Client Data playerlist unavailable: {ex.Message}");
            return [];
        }
    }

    public async Task<string?> GetActivePlayerNameAsync()
    {
        try
        {
            var player = await HttpClient.GetFromJsonAsync<LiveClientPlayer>(ActivePlayerUrl);
            if (player is null)
            {
                return null;
            }

            return FirstNonEmpty(player.RiotIdGameName, player.SummonerName, StripRiotTag(player.RiotId));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Trace.TraceError($"Live Client Data activeplayer unavailable: {ex.Message}");
            return null;
        }
    }

    private static CurrentPlayerInfo ToCurrentPlayerInfo(LiveClientPlayer player)
    {
        var playerName = FirstNonEmpty(player.RiotIdGameName, player.SummonerName, StripRiotTag(player.RiotId));

        return new CurrentPlayerInfo
        {
            RiotId = player.RiotId ?? string.Empty,
            PlayerName = playerName,
            ChampionName = player.ChampionName ?? string.Empty,
            RawChampionName = player.RawChampionName ?? string.Empty
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static string StripRiotTag(string? riotId)
    {
        if (string.IsNullOrWhiteSpace(riotId))
        {
            return string.Empty;
        }

        var hashIndex = riotId.IndexOf('#');
        return hashIndex > 0 ? riotId[..hashIndex] : riotId;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, _, _, _) =>
            {
                var host = request?.RequestUri?.Host;
                return string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
            }
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
    }

    private sealed class LiveClientPlayer
    {
        [JsonPropertyName("riotId")]
        public string? RiotId { get; set; }

        [JsonPropertyName("riotIdGameName")]
        public string? RiotIdGameName { get; set; }

        [JsonPropertyName("summonerName")]
        public string? SummonerName { get; set; }

        [JsonPropertyName("championName")]
        public string? ChampionName { get; set; }

        [JsonPropertyName("rawChampionName")]
        public string? RawChampionName { get; set; }
    }
}
