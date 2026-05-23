namespace LoLChatTranslator.Services;

internal static class TextSimilarity
{
    public static double NormalizedSimilarity(string left, string right)
    {
        var maxLength = Math.Max(left.Length, right.Length);
        if (maxLength == 0)
        {
            return 1;
        }

        var distance = LevenshteinDistance(left, right);
        return 1.0 - ((double)distance / maxLength);
    }

    public static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        if (left.Length > right.Length)
        {
            (left, right) = (right, left);
        }

        var previous = new int[left.Length + 1];
        var current = new int[left.Length + 1];

        for (var i = 0; i <= left.Length; i++)
        {
            previous[i] = i;
        }

        for (var j = 1; j <= right.Length; j++)
        {
            current[0] = j;

            for (var i = 1; i <= left.Length; i++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[i] = Math.Min(
                    Math.Min(previous[i] + 1, current[i - 1] + 1),
                    previous[i - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[left.Length];
    }
}
