namespace LoLChatTranslator.Services;

public static class OcrMode
{
    public const string Stable = "stable";
    public const string Fast = "fast";
    public const string Standard = Stable;
    public const string Accurate = "accurate";
    public const string Experimental = "experimental";

    public static string Normalize(string? mode)
    {
        if (string.Equals(mode, Stable, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "standard", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "balanced", StringComparison.OrdinalIgnoreCase))
        {
            return Stable;
        }

        if (string.Equals(mode, Fast, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "quick", StringComparison.OrdinalIgnoreCase))
        {
            return Fast;
        }

        if (string.Equals(mode, Accurate, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "high", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "high_accuracy", StringComparison.OrdinalIgnoreCase))
        {
            return Accurate;
        }

        if (string.Equals(mode, Experimental, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "exp", StringComparison.OrdinalIgnoreCase))
        {
            return Experimental;
        }

        return Stable;
    }
}
