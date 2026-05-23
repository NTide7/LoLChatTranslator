using System.Windows;

namespace LoLChatTranslator.Models;

public sealed class OcrTextLine
{
    public string Text { get; set; } = string.Empty;

    public double Confidence { get; set; }

    public Rect? BoundingBox { get; set; }

    public int RawIndex { get; set; } = -1;

    public int VisualOrder { get; set; } = -1;
}
