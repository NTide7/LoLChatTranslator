using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public sealed record OcrTriggerResult(
    bool ShouldRunOcr,
    Bitmap? OcrSnapshot,
    long CropMs = 0,
    long MaskMs = 0,
    string Reason = "",
    IReadOnlyList<ulong>? NewLineHashes = null,
    IReadOnlyList<Rectangle>? DirtyRegions = null,
    bool UseFullOcr = true,
    string? FullRescanReason = null,
    int DirtyLineCount = 0,
    int ChangedPixels = 0,
    double DirtyRatio = 0,
    bool TextMaskChanged = false);

public sealed class OcrTextMaskTrigger : IDisposable
{
    private const int MaxRememberedLineHashes = 512;

    private TextMaskFrame? _previousFrame;
    private TextMaskFrame? _pendingFrame;
    private DateTimeOffset _lastTriggerTime = DateTimeOffset.MinValue;
    private readonly Queue<ulong> _rememberedOrder = new();
    private readonly HashSet<ulong> _rememberedLineHashes = [];

    public OcrTriggerResult Evaluate(Bitmap chatFrame, OcrConfig config)
    {
        var cropStopwatch = Stopwatch.StartNew();
        cropStopwatch.Stop();

        var maskStopwatch = Stopwatch.StartNew();
        var currentFrame = TextMaskFrame.Create(chatFrame);
        maskStopwatch.Stop();

        if (currentFrame.InkPixels < Math.Max(12, chatFrame.Width / 18))
        {
            currentFrame.Dispose();
            return new OcrTriggerResult(false, null, cropStopwatch.ElapsedMilliseconds, maskStopwatch.ElapsedMilliseconds, "no_ocr_lines");
        }

        var diff = _previousFrame is null
            ? new TextMaskDiff(chatFrame.Width * chatFrame.Height, 1D)
            : currentFrame.Diff(_previousFrame);
        var newLines = currentFrame.Lines
            .Where(line => !_rememberedLineHashes.Contains(line.Hash))
            .ToList();

        if (_previousFrame is not null
            && diff.ChangedPixels < Math.Max(1, config.MinTextMaskChangedPixels))
        {
            currentFrame.Dispose();
            return new OcrTriggerResult(
                false,
                null,
                cropStopwatch.ElapsedMilliseconds,
                maskStopwatch.ElapsedMilliseconds,
                "mask_no_change",
                ChangedPixels: diff.ChangedPixels,
                DirtyRatio: diff.Ratio,
                TextMaskChanged: false);
        }

        if (!config.EnableAdaptiveDirtyRegionOcr
            && config.EnableTextMaskDetection
            && _previousFrame is not null
            && diff.Ratio < config.TextMaskDiffThreshold)
        {
            currentFrame.Dispose();
            return new OcrTriggerResult(
                false,
                null,
                cropStopwatch.ElapsedMilliseconds,
                maskStopwatch.ElapsedMilliseconds,
                "mask_no_change",
                ChangedPixels: diff.ChangedPixels,
                DirtyRatio: diff.Ratio,
                TextMaskChanged: false);
        }

        if (!config.EnableAdaptiveDirtyRegionOcr && newLines.Count == 0 && _previousFrame is not null)
        {
            currentFrame.Dispose();
            return new OcrTriggerResult(
                false,
                null,
                cropStopwatch.ElapsedMilliseconds,
                maskStopwatch.ElapsedMilliseconds,
                "mask_no_change",
                ChangedPixels: diff.ChangedPixels,
                DirtyRatio: diff.Ratio,
                TextMaskChanged: false);
        }

        var now = DateTimeOffset.UtcNow;
        var minimumInterval = TimeSpan.FromMilliseconds(Math.Max(300, config.OcrTriggerMinIntervalMs));
        if (now - _lastTriggerTime < minimumInterval)
        {
            currentFrame.Dispose();
            return new OcrTriggerResult(false, null, cropStopwatch.ElapsedMilliseconds, maskStopwatch.ElapsedMilliseconds, "cooldown");
        }

        var useFullOcr = true;
        var fullRescanReason = _previousFrame is null ? "initial_full_scan" : string.Empty;
        var dirtyRegions = Array.Empty<Rectangle>();
        var dirtyLineCount = 0;

        if (config.EnableAdaptiveDirtyRegionOcr && _previousFrame is not null)
        {
            dirtyRegions = currentFrame.BuildDirtyLineRegions(_previousFrame, config, out dirtyLineCount);
            if (dirtyRegions.Length == 0 && newLines.Count > 0)
            {
                dirtyRegions = currentFrame.BuildLineRegions(newLines, config, out dirtyLineCount);
            }

            useFullOcr =
                dirtyRegions.Length == 0
                || diff.Ratio > config.MaxDirtyRegionRatioBeforeFullScan
                || dirtyLineCount > Math.Max(1, config.DirtyLineBatchSize);

            fullRescanReason = useFullOcr
                ? dirtyRegions.Length == 0
                    ? "dirty_region_unmapped"
                    : diff.Ratio > config.MaxDirtyRegionRatioBeforeFullScan
                        ? "dirty_region_too_large"
                        : "dirty_line_batch_limit"
                : string.Empty;
        }

        var snapshot = useFullOcr
            ? Clone(chatFrame)
            : CropMergedDirtyRegions(chatFrame, dirtyRegions, out cropStopwatch);
        var newLineHashes = newLines.Select(line => line.Hash).ToArray();
        ReplacePendingFrame(currentFrame);
        return new OcrTriggerResult(
            true,
            snapshot,
            cropStopwatch.ElapsedMilliseconds,
            maskStopwatch.ElapsedMilliseconds,
            useFullOcr ? "textmask_full_scan" : "dirty_line_changed",
            newLineHashes,
            dirtyRegions,
            useFullOcr,
            string.IsNullOrWhiteSpace(fullRescanReason) ? null : fullRescanReason,
            dirtyLineCount,
            diff.ChangedPixels,
            diff.Ratio,
            TextMaskChanged: true);
    }

    public static Bitmap CropBottom(Bitmap chatFrame, OcrConfig config)
    {
        if (config.EnableFixedBottomOcr)
        {
            var height = Math.Clamp(config.RealtimeBottomHeight, 40, chatFrame.Height);
            return Crop(chatFrame, new Rectangle(0, chatFrame.Height - height, chatFrame.Width, height));
        }

        // Default behavior keeps the full user-selected OCR region; this clone only protects ownership/disposal.
        return Clone(chatFrame);
    }

    public void Reset()
    {
        _previousFrame?.Dispose();
        _previousFrame = null;
        _pendingFrame?.Dispose();
        _pendingFrame = null;
        _lastTriggerTime = DateTimeOffset.MinValue;
        _rememberedOrder.Clear();
        _rememberedLineHashes.Clear();
    }

    public void RememberLineHashes(IEnumerable<ulong>? hashes)
    {
        if (hashes is null)
        {
            return;
        }

        foreach (var hash in hashes)
        {
            if (_rememberedLineHashes.Add(hash))
            {
                _rememberedOrder.Enqueue(hash);
            }
        }

        while (_rememberedOrder.Count > MaxRememberedLineHashes)
        {
            _rememberedLineHashes.Remove(_rememberedOrder.Dequeue());
        }
    }

    public void Dispose()
    {
        Reset();
    }

    public void CommitPendingFrame(IEnumerable<ulong>? hashes, string reason)
    {
        if (_pendingFrame is null)
        {
            return;
        }

        ReplacePreviousFrame(_pendingFrame);
        _pendingFrame = null;
        _lastTriggerTime = DateTimeOffset.UtcNow;
        RememberLineHashes(hashes);
        WriteCommitLog(committed: true, reason);
    }

    public void RollbackPendingFrame(string reason)
    {
        if (_pendingFrame is null)
        {
            return;
        }

        _pendingFrame.Dispose();
        _pendingFrame = null;
        WriteCommitLog(committed: false, reason);
    }

    private void ReplacePreviousFrame(TextMaskFrame frame)
    {
        _previousFrame?.Dispose();
        _previousFrame = frame;
    }

    private void ReplacePendingFrame(TextMaskFrame frame)
    {
        _pendingFrame?.Dispose();
        _pendingFrame = frame;
    }

    private static void WriteCommitLog(bool committed, string reason)
    {
        try
        {
            AppLogService.AppendVerboseText(
                "text-mask-debug.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} text_mask_frame_committed={committed.ToString().ToLowerInvariant()} commit_reason={reason}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics only.
        }
    }

    private static Bitmap Crop(Bitmap source, Rectangle rectangle)
    {
        var result = new Bitmap(Math.Max(1, rectangle.Width), Math.Max(1, rectangle.Height));
        using var graphics = Graphics.FromImage(result);
        graphics.DrawImage(source, new Rectangle(0, 0, result.Width, result.Height), rectangle, GraphicsUnit.Pixel);
        return result;
    }

    private static Bitmap CropMergedDirtyRegions(Bitmap source, IReadOnlyList<Rectangle> regions, out Stopwatch stopwatch)
    {
        stopwatch = Stopwatch.StartNew();
        var merged = MergeToBoundingRegion(regions, source.Width, source.Height);
        var result = Crop(source, merged);
        stopwatch.Stop();
        return result;
    }

    private static Rectangle MergeToBoundingRegion(IReadOnlyList<Rectangle> regions, int width, int height)
    {
        if (regions.Count == 0)
        {
            return new Rectangle(0, 0, width, height);
        }

        var left = regions.Min(region => region.Left);
        var top = regions.Min(region => region.Top);
        var right = regions.Max(region => region.Right);
        var bottom = regions.Max(region => region.Bottom);
        return Rectangle.Intersect(
            new Rectangle(0, 0, width, height),
            Rectangle.FromLTRB(left, top, right, bottom));
    }

    private static Bitmap Clone(Bitmap source)
    {
        return Crop(source, new Rectangle(0, 0, source.Width, source.Height));
    }
}

internal sealed record TextMaskLine(int Y, int Height, ulong Hash);

internal sealed record TextMaskDiff(int ChangedPixels, double Ratio);

internal sealed class TextMaskFrame : IDisposable
{
    private readonly byte[] _mask;

    private TextMaskFrame(int width, int height, byte[] mask, List<TextMaskLine> lines)
    {
        Width = width;
        Height = height;
        _mask = mask;
        Lines = lines;
        InkPixels = mask.Count(value => value != 0);
    }

    public int Width { get; }

    public int Height { get; }

    public int InkPixels { get; }

    public List<TextMaskLine> Lines { get; }

    public static TextMaskFrame Create(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var gray = BuildGray(bitmap);
        var rawMask = BuildRawMask(gray, width, height);
        var filteredMask = FilterTextComponents(rawMask, width, height);
        var mask = Dilate(filteredMask, width, height);
        return new TextMaskFrame(width, height, mask, DetectLines(mask, width, height));
    }

    public TextMaskDiff Diff(TextMaskFrame other)
    {
        if (Width != other.Width || Height != other.Height)
        {
            return new TextMaskDiff(Width * Height, 1D);
        }

        var changed = 0;
        for (var i = 0; i < _mask.Length; i++)
        {
            if (_mask[i] != other._mask[i])
            {
                changed++;
            }
        }

        return new TextMaskDiff(changed, changed / (double)Math.Max(Math.Max(InkPixels, other.InkPixels), 1));
    }

    public Rectangle[] BuildDirtyLineRegions(TextMaskFrame previous, OcrConfig config, out int dirtyLineCount)
    {
        dirtyLineCount = 0;
        if (Width != previous.Width || Height != previous.Height)
        {
            return [];
        }

        var changedRows = new bool[Height];
        var rowThreshold = Math.Max(2, Width / 180);
        for (var y = 0; y < Height; y++)
        {
            var row = y * Width;
            var changedInRow = 0;
            for (var x = 0; x < Width; x++)
            {
                if (_mask[row + x] != previous._mask[row + x])
                {
                    changedInRow++;
                }
            }

            changedRows[y] = changedInRow >= rowThreshold;
        }

        var candidateIndexes = new SortedSet<int>();
        for (var i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            var start = Math.Max(0, line.Y - config.DirtyRegionPaddingY);
            var end = Math.Min(Height - 1, line.Y + line.Height + config.DirtyRegionPaddingY);
            var overlapsChanged = false;
            for (var y = start; y <= end; y++)
            {
                if (!changedRows[y])
                {
                    continue;
                }

                overlapsChanged = true;
                break;
            }

            if (!overlapsChanged)
            {
                continue;
            }

            candidateIndexes.Add(i);
            if (config.DirtyLineIncludeNeighborLines)
            {
                if (i > 0)
                {
                    candidateIndexes.Add(i - 1);
                }

                if (i + 1 < Lines.Count)
                {
                    candidateIndexes.Add(i + 1);
                }
            }
        }

        if (candidateIndexes.Count == 0)
        {
            return BuildRegionsFromChangedRows(changedRows, config, out dirtyLineCount);
        }

        dirtyLineCount = candidateIndexes.Count;
        return MergeVerticalRegions(
            candidateIndexes.Select(index => BuildLineRegion(Lines[index], config)).ToList(),
            Width,
            Height);
    }

    public Rectangle[] BuildLineRegions(IReadOnlyList<TextMaskLine> lines, OcrConfig config, out int dirtyLineCount)
    {
        dirtyLineCount = lines.Count;
        return MergeVerticalRegions(lines.Select(line => BuildLineRegion(line, config)).ToList(), Width, Height);
    }

    public void Dispose()
    {
    }

    public Bitmap CreateMaskPreview()
    {
        var result = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var bounds = new Rectangle(0, 0, Width, Height);
        var data = result.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowLength = Math.Abs(data.Stride);
            var row = new byte[rowLength];
            for (var y = 0; y < Height; y++)
            {
                Array.Clear(row);
                var maskRow = y * Width;
                for (var x = 0; x < Width; x++)
                {
                    var index = x * 4;
                    var value = _mask[maskRow + x] != 0 ? (byte)255 : (byte)0;
                    row[index] = value;
                    row[index + 1] = value;
                    row[index + 2] = value;
                    row[index + 3] = 255;
                }

                Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), rowLength);
            }
        }
        finally
        {
            result.UnlockBits(data);
        }

        return result;
    }

    public Bitmap CreateDiffPreview(TextMaskFrame? previous)
    {
        if (previous is null || previous.Width != Width || previous.Height != Height)
        {
            return CreateMaskPreview();
        }

        var result = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var bounds = new Rectangle(0, 0, Width, Height);
        var data = result.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowLength = Math.Abs(data.Stride);
            var row = new byte[rowLength];
            for (var y = 0; y < Height; y++)
            {
                Array.Clear(row);
                var maskRow = y * Width;
                for (var x = 0; x < Width; x++)
                {
                    var index = x * 4;
                    var changed = _mask[maskRow + x] != previous._mask[maskRow + x];
                    row[index] = changed ? (byte)32 : (byte)0;
                    row[index + 1] = changed ? (byte)96 : (byte)0;
                    row[index + 2] = changed ? (byte)255 : (byte)0;
                    row[index + 3] = 255;
                }

                Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), rowLength);
            }
        }
        finally
        {
            result.UnlockBits(data);
        }

        return result;
    }

    private static byte[] BuildGray(Bitmap bitmap)
    {
        var gray = new byte[bitmap.Width * bitmap.Height];
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        using var normalized = bitmap.PixelFormat == PixelFormat.Format32bppArgb
            ? null
            : bitmap.Clone(bounds, PixelFormat.Format32bppArgb);
        var image = normalized ?? bitmap;
        var data = image.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowLength = Math.Abs(data.Stride);
            var row = new byte[rowLength];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, rowLength);
                var grayRow = y * bitmap.Width;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var index = x * 4;
                    gray[grayRow + x] = (byte)((row[index + 2] * 30 + row[index + 1] * 59 + row[index] * 11) / 100);
                }
            }
        }
        finally
        {
            image.UnlockBits(data);
        }

        return gray;
    }

    private static byte[] BuildRawMask(byte[] gray, int width, int height)
    {
        var mask = new byte[gray.Length];
        var integral = BuildIntegralImage(gray, width, height);
        var radius = 4;
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var idx = y * width + x;
                var localMean = LocalMean(integral, width, height, x, y, radius);
                var gx = Math.Abs(gray[idx + 1] - gray[idx - 1]);
                var gy = Math.Abs(gray[idx + width] - gray[idx - width]);
                var edge = gx + gy;

                if (gray[idx] >= localMean + 18 && gray[idx] >= 118 && edge >= 22)
                {
                    mask[idx] = 1;
                }
            }
        }

        return mask;
    }

    private static long[] BuildIntegralImage(byte[] gray, int width, int height)
    {
        var integral = new long[(width + 1) * (height + 1)];
        for (var y = 0; y < height; y++)
        {
            long rowSum = 0;
            var sourceRow = y * width;
            var integralRow = (y + 1) * (width + 1);
            var previousIntegralRow = y * (width + 1);
            for (var x = 0; x < width; x++)
            {
                rowSum += gray[sourceRow + x];
                integral[integralRow + x + 1] = integral[previousIntegralRow + x + 1] + rowSum;
            }
        }

        return integral;
    }

    private static int LocalMean(long[] integral, int width, int height, int centerX, int centerY, int radius)
    {
        var left = Math.Max(0, centerX - radius);
        var right = Math.Min(width - 1, centerX + radius);
        var top = Math.Max(0, centerY - radius);
        var bottom = Math.Min(height - 1, centerY + radius);
        var stride = width + 1;
        var sum =
            integral[(bottom + 1) * stride + right + 1]
            - integral[top * stride + right + 1]
            - integral[(bottom + 1) * stride + left]
            + integral[top * stride + left];
        var count = (right - left + 1) * (bottom - top + 1);

        return count == 0 ? 0 : (int)(sum / count);
    }

    private static byte[] FilterTextComponents(byte[] rawMask, int width, int height)
    {
        var result = new byte[rawMask.Length];
        var visited = new bool[rawMask.Length];
        var queue = new Queue<int>();
        var component = new List<int>();
        var maxArea = Math.Max(80, width * height / 20);

        for (var i = 0; i < rawMask.Length; i++)
        {
            if (rawMask[i] == 0 || visited[i])
            {
                continue;
            }

            component.Clear();
            queue.Enqueue(i);
            visited[i] = true;
            var minX = width;
            var maxX = 0;
            var minY = height;
            var maxY = 0;

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(current);
                var x = current % width;
                var y = current / width;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);

                foreach (var neighbor in GetNeighbors(current, x, y, width, height))
                {
                    if (rawMask[neighbor] != 0 && !visited[neighbor])
                    {
                        visited[neighbor] = true;
                        queue.Enqueue(neighbor);
                    }
                }
            }

            var area = component.Count;
            var boxWidth = maxX - minX + 1;
            var boxHeight = maxY - minY + 1;
            if (area is >= 2
                && area <= maxArea
                && boxHeight is >= 2 and <= 36
                && boxWidth <= width * 0.75)
            {
                foreach (var pixel in component)
                {
                    result[pixel] = 1;
                }
            }
        }

        return result;
    }

    private static IEnumerable<int> GetNeighbors(int current, int x, int y, int width, int height)
    {
        if (x > 0)
        {
            yield return current - 1;
        }

        if (x < width - 1)
        {
            yield return current + 1;
        }

        if (y > 0)
        {
            yield return current - width;
        }

        if (y < height - 1)
        {
            yield return current + width;
        }
    }

    private static byte[] Dilate(byte[] mask, int width, int height)
    {
        var result = new byte[mask.Length];
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var idx = y * width + x;
                if (mask[idx] == 0)
                {
                    continue;
                }

                result[idx] = 1;
                result[idx - 1] = 1;
                result[idx + 1] = 1;
                result[idx - width] = 1;
                result[idx + width] = 1;
            }
        }

        return result;
    }

    private static List<TextMaskLine> DetectLines(byte[] mask, int width, int height)
    {
        var rowCounts = new int[height];
        for (var y = 0; y < height; y++)
        {
            var count = 0;
            for (var x = 0; x < width; x++)
            {
                count += mask[y * width + x] != 0 ? 1 : 0;
            }

            rowCounts[y] = count;
        }

        var threshold = Math.Max(3, width / 90);
        var lines = new List<(int Start, int End)>();
        var start = -1;
        var lastInk = -1;

        for (var y = 0; y < height; y++)
        {
            if (rowCounts[y] >= threshold)
            {
                if (start < 0)
                {
                    start = y;
                }

                lastInk = y;
            }
            else if (start >= 0 && y - lastInk > 3)
            {
                AddLine(lines, start, lastInk);
                start = -1;
                lastInk = -1;
            }
        }

        if (start >= 0)
        {
            AddLine(lines, start, lastInk);
        }

        return lines
            .Select(line => new TextMaskLine(line.Start, line.End - line.Start + 1, HashLine(mask, width, line.Start, line.End)))
            .ToList();
    }

    private static void AddLine(List<(int Start, int End)> lines, int start, int end)
    {
        if (end - start + 1 is >= 4 and <= 42)
        {
            lines.Add((start, end));
        }
    }

    private static ulong HashLine(byte[] mask, int width, int startY, int endY)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;

        for (var y = startY; y <= endY; y++)
        {
            var row = y * width;
            var bucket = 0;
            for (var x = 0; x < width; x += 4)
            {
                var value = 0;
                for (var dx = 0; dx < 4 && x + dx < width; dx++)
                {
                    value += mask[row + x + dx] != 0 ? 1 : 0;
                }

                bucket = (bucket << 1) ^ value;
            }

            hash ^= (uint)bucket;
            hash *= prime;
        }

        return hash;
    }

    private Rectangle BuildLineRegion(TextMaskLine line, OcrConfig config)
    {
        var left = 0;
        var top = Math.Max(0, line.Y - Math.Max(0, config.DirtyRegionPaddingY));
        var right = Width;
        var bottom = Math.Min(Height, line.Y + line.Height + Math.Max(0, config.DirtyRegionPaddingY));
        return Rectangle.FromLTRB(left, top, right, Math.Max(top + 1, bottom));
    }

    private Rectangle[] BuildRegionsFromChangedRows(bool[] changedRows, OcrConfig config, out int dirtyLineCount)
    {
        var regions = new List<Rectangle>();
        var start = -1;
        var lastChanged = -1;
        for (var y = 0; y < changedRows.Length; y++)
        {
            if (changedRows[y])
            {
                if (start < 0)
                {
                    start = y;
                }

                lastChanged = y;
                continue;
            }

            if (start >= 0 && y - lastChanged > 3)
            {
                regions.Add(BuildRowRegion(start, lastChanged, config));
                start = -1;
                lastChanged = -1;
            }
        }

        if (start >= 0)
        {
            regions.Add(BuildRowRegion(start, lastChanged, config));
        }

        dirtyLineCount = regions.Count;
        return MergeVerticalRegions(regions, Width, Height);
    }

    private Rectangle BuildRowRegion(int startY, int endY, OcrConfig config)
    {
        var top = Math.Max(0, startY - Math.Max(0, config.DirtyRegionPaddingY));
        var bottom = Math.Min(Height, endY + 1 + Math.Max(0, config.DirtyRegionPaddingY));
        return Rectangle.FromLTRB(0, top, Width, Math.Max(top + 1, bottom));
    }

    private static Rectangle[] MergeVerticalRegions(IReadOnlyList<Rectangle> source, int width, int height)
    {
        if (source.Count == 0)
        {
            return [];
        }

        var ordered = source
            .Select(region => Rectangle.Intersect(new Rectangle(0, 0, width, height), region))
            .Where(region => region.Width > 0 && region.Height > 0)
            .OrderBy(region => region.Top)
            .ToList();

        var result = new List<Rectangle>();
        foreach (var region in ordered)
        {
            if (result.Count == 0)
            {
                result.Add(region);
                continue;
            }

            var last = result[^1];
            if (region.Top <= last.Bottom + 4)
            {
                result[^1] = Rectangle.FromLTRB(
                    Math.Min(last.Left, region.Left),
                    Math.Min(last.Top, region.Top),
                    Math.Max(last.Right, region.Right),
                    Math.Max(last.Bottom, region.Bottom));
                continue;
            }

            result.Add(region);
        }

        return result.ToArray();
    }
}
