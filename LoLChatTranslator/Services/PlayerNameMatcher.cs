using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public sealed class PlayerNameMatcher
{
    private const double MatchThreshold = 0.78;
    private const double UserNameWeight = 0.75;
    private const double ChampionWeight = 0.25;

    public CurrentPlayerInfo? Match(
        string? ocrPlayerName,
        string? ocrChampionText,
        IReadOnlyList<CurrentPlayerInfo> players)
    {
        if (string.IsNullOrWhiteSpace(ocrPlayerName) || players.Count == 0)
        {
            return null;
        }

        CurrentPlayerInfo? bestPlayer = null;
        var bestScore = 0.0;

        foreach (var player in players)
        {
            var userScore = CalculateUserNameScore(ocrPlayerName, player);
            var finalScore = userScore;

            if (!string.IsNullOrWhiteSpace(ocrChampionText))
            {
                var championScore = CalculateChampionScore(ocrChampionText, player);
                finalScore = (userScore * UserNameWeight) + (championScore * ChampionWeight);
            }

            if (finalScore > bestScore)
            {
                bestScore = finalScore;
                bestPlayer = player;
            }
        }

        return bestScore >= MatchThreshold ? bestPlayer : null;
    }

    private static double CalculateUserNameScore(string ocrPlayerName, CurrentPlayerInfo player)
    {
        return GetPlayerNameCandidates(player)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => CalculateSimilarity(ocrPlayerName, candidate))
            .DefaultIfEmpty(0)
            .Max();
    }

    private static IEnumerable<string> GetPlayerNameCandidates(CurrentPlayerInfo player)
    {
        yield return player.PlayerName;
        yield return player.RiotId;
        yield return StripRiotTag(player.RiotId);
    }

    private static double CalculateChampionScore(string ocrChampionText, CurrentPlayerInfo player)
    {
        var candidates = player.ChampionAliases
            .Concat([player.ChampionName, player.RawChampionName])
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate));

        return candidates
            .Select(candidate => CalculateSimilarity(ocrChampionText, candidate))
            .DefaultIfEmpty(0)
            .Max();
    }

    private static string StripRiotTag(string riotId)
    {
        var hashIndex = riotId.IndexOf('#');
        return hashIndex > 0 ? riotId[..hashIndex] : riotId;
    }

    private static double CalculateSimilarity(string left, string right)
    {
        var normalizedLeft = NormalizeForMatch(left);
        var normalizedRight = NormalizeForMatch(right);

        if (string.IsNullOrWhiteSpace(normalizedLeft) || string.IsNullOrWhiteSpace(normalizedRight))
        {
            return 0;
        }

        if (normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (normalizedLeft.Contains(normalizedRight, StringComparison.OrdinalIgnoreCase)
            || normalizedRight.Contains(normalizedLeft, StringComparison.OrdinalIgnoreCase))
        {
            var min = Math.Min(normalizedLeft.Length, normalizedRight.Length);
            var max = Math.Max(normalizedLeft.Length, normalizedRight.Length);
            return max == 0 ? 0 : (double)min / max;
        }

        return TextSimilarity.NormalizedSimilarity(normalizedLeft, normalizedRight);
    }

    private static string NormalizeForMatch(string value)
    {
        var chars = value
            .Where(char.IsLetterOrDigit)
            .Select(NormalizeOcrConfusable)
            .ToArray();

        return new string(chars).ToLowerInvariant();
    }

    private static char NormalizeOcrConfusable(char value)
    {
        return value switch
        {
            'Ｉ' or 'Ⅰ' or 'l' or 'I' => 'i',
            'Ｏ' or 'O' or 'o' => '0',
            _ => value
        };
    }

}
