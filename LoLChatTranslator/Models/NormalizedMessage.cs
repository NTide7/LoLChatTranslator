namespace LoLChatTranslator.Models;

public sealed class NormalizedMessage
{
    public string Original { get; set; } = string.Empty;

    public string NormalizedText { get; set; } = string.Empty;

    public string? DirectTranslation { get; set; }

    public bool IsTrustedDirectOutput { get; init; }

    public string DirectOutputKind { get; init; } = "none";

    public bool GlossaryMatched { get; set; }

    public string GlossaryMatchLevel { get; set; } = "none";

    public double GlossaryConfidence { get; set; }

    public string? GlossaryMatchedEntry { get; set; }

    public bool ShouldBypassTranslator => !string.IsNullOrWhiteSpace(DirectTranslation);

    public bool ShouldCallTranslator => !ShouldBypassTranslator;
}
