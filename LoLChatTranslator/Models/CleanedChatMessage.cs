namespace LoLChatTranslator.Models;

public sealed class CleanedChatMessage
{
    public string RawLine { get; set; } = string.Empty;

    public string? Timestamp { get; set; }

    public ChatChannel Channel { get; set; } = ChatChannel.Unknown;

    public string? RawChannelText { get; set; }

    public string? OcrPlayerName { get; set; }

    public string? OcrChampionText { get; set; }

    public string? FixedPlayerName { get; set; }

    public string? FixedChampionName { get; set; }

    public string RawMessageBody { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public int SourceOrder { get; set; } = int.MaxValue;

    public double? SourceTop { get; set; }

    public double? SourceLeft { get; set; }

    public int SourceRawLineIndex { get; set; } = -1;
}
