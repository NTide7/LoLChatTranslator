using System.Windows;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public sealed record ReadingOrderResult(
    List<OcrTextLine> Lines,
    string Mode);

public static class ReadingOrderService
{
    public static ReadingOrderResult Sort(IReadOnlyList<OcrTextLine> lines)
    {
        if (lines.Count == 0)
        {
            return new ReadingOrderResult([], "none");
        }

        var indexed = lines
            .Select((line, index) => new IndexedLine(line, index, line.RawIndex >= 0 ? line.RawIndex : index))
            .ToList();

        var validBoxes = indexed
            .Where(item => IsValidBox(item.Line.BoundingBox))
            .Select(item => item.Line.BoundingBox!.Value)
            .ToList();

        if (validBoxes.Count == 0)
        {
            return new ReadingOrderResult(
                indexed.Select((item, visualOrder) => CloneWithOrder(item.Line, item.RawIndex, visualOrder)).ToList(),
                "raw_fallback");
        }

        var medianHeight = Median(validBoxes.Select(box => box.Height));
        var rowTolerance = Math.Max(6, medianHeight * 0.6);
        var rows = new List<ReadingRow>();

        foreach (var item in indexed.OrderBy(item => BoxTopOrMax(item.Line.BoundingBox))
                     .ThenBy(item => BoxLeftOrMax(item.Line.BoundingBox))
                     .ThenBy(item => item.RawIndex))
        {
            if (!IsValidBox(item.Line.BoundingBox))
            {
                rows.Add(new ReadingRow(double.MaxValue, [item]));
                continue;
            }

            var top = item.Line.BoundingBox!.Value.Top;
            var row = rows.FirstOrDefault(candidate =>
                candidate.Top < double.MaxValue && Math.Abs(candidate.Top - top) <= rowTolerance);
            if (row is null)
            {
                rows.Add(new ReadingRow(top, [item]));
                continue;
            }

            row.Items.Add(item);
            row.Top = Median(row.Items
                .Where(rowItem => IsValidBox(rowItem.Line.BoundingBox))
                .Select(rowItem => rowItem.Line.BoundingBox!.Value.Top));
        }

        var ordered = new List<IndexedLine>();
        foreach (var row in rows.OrderBy(row => row.Top))
        {
            ordered.AddRange(row.Items
                .OrderBy(item => BoxLeftOrMax(item.Line.BoundingBox))
                .ThenBy(item => item.RawIndex));
        }

        return new ReadingOrderResult(
            ordered.Select((item, visualOrder) => CloneWithOrder(item.Line, item.RawIndex, visualOrder)).ToList(),
            "box_sort");
    }

    private static OcrTextLine CloneWithOrder(OcrTextLine line, int rawIndex, int visualOrder)
    {
        return new OcrTextLine
        {
            Text = line.Text,
            Confidence = line.Confidence,
            BoundingBox = line.BoundingBox,
            RawIndex = rawIndex,
            VisualOrder = visualOrder
        };
    }

    private static bool IsValidBox(Rect? box)
    {
        return box is { Width: > 0, Height: > 0 };
    }

    private static double BoxTopOrMax(Rect? box)
    {
        return IsValidBox(box) ? box!.Value.Top : double.MaxValue;
    }

    private static double BoxLeftOrMax(Rect? box)
    {
        return IsValidBox(box) ? box!.Value.Left : double.MaxValue;
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values
            .Where(value => !double.IsNaN(value) && !double.IsInfinity(value))
            .OrderBy(value => value)
            .ToArray();
        if (ordered.Length == 0)
        {
            return 10;
        }

        var midpoint = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[midpoint]
            : (ordered[midpoint - 1] + ordered[midpoint]) / 2;
    }

    private sealed record IndexedLine(OcrTextLine Line, int InputIndex, int RawIndex);

    private sealed class ReadingRow(double top, List<IndexedLine> items)
    {
        public double Top { get; set; } = top;

        public List<IndexedLine> Items { get; } = items;
    }
}
