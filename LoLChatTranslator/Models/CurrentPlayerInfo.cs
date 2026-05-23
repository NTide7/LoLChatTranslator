namespace LoLChatTranslator.Models;

public sealed class CurrentPlayerInfo
{
    public string RiotId { get; set; } = string.Empty;

    public string PlayerName { get; set; } = string.Empty;

    public string ChampionName { get; set; } = string.Empty;

    public string RawChampionName { get; set; } = string.Empty;

    public List<string> ChampionAliases { get; set; } = [];
}
