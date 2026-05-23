using System.IO;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using LoLChatTranslator.Models;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingBrushes = System.Drawing.Brushes;
using DrawingColor = System.Drawing.Color;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingPen = System.Drawing.Pen;
using DrawingRectangle = System.Drawing.Rectangle;

namespace LoLChatTranslator.Services;

public sealed record OcrSelectionDebugInfo(
    Rect SelectedRectUi,
    Rect SelectedRectScreen,
    Int32Rect SelectedRectPhysical,
    double DpiScaleX,
    double DpiScaleY);

public sealed record OcrCaptureDebugResult(
    string DirectoryPath,
    string FullScreenshotPath,
    string AnnotatedScreenshotPath,
    string LatestCropPath,
    string DebugInfoPath,
    string DebugText);

public static class OcrCaptureDebugService
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint MonitorDefaultToNearest = 0x00000002;

    public static OcrCaptureDebugResult SaveDebugImages(
        OcrConfig config,
        OcrSelectionDebugInfo? selectionDebugInfo = null)
    {
        var debugDirectory = ResolveDebugDirectory();

        var fullScreenshotPath = Path.Combine(debugDirectory, "full_screenshot.png");
        var annotatedScreenshotPath = Path.Combine(debugDirectory, "full_screenshot_with_selected_rect.png");
        var latestCropPath = Path.Combine(debugDirectory, "latest_crop.png");
        var debugInfoPath = Path.Combine(debugDirectory, "capture-debug.txt");

        var virtualRect = GetVirtualScreenRect();
        var captureRect = new Int32Rect(
            config.RegionX,
            config.RegionY,
            Math.Max(1, config.RegionWidth),
            Math.Max(1, config.RegionHeight));
        var monitorRect = GetMonitorRect(captureRect);
        var dpiScale = GetMonitorDpiScale(captureRect);

        using var fullScreenshot = CaptureScreenRect(virtualRect);
        fullScreenshot.Save(fullScreenshotPath, ImageFormat.Png);

        using var annotated = (DrawingBitmap)fullScreenshot.Clone();
        DrawCaptureRectangle(annotated, virtualRect, captureRect);
        annotated.Save(annotatedScreenshotPath, ImageFormat.Png);

        using var crop = CaptureScreenRect(captureRect);
        crop.Save(latestCropPath, ImageFormat.Png);

        var debugText = BuildDebugText(
            selectionDebugInfo,
            dpiScale,
            virtualRect,
            monitorRect,
            captureRect,
            crop.Width,
            crop.Height,
            fullScreenshotPath,
            annotatedScreenshotPath,
            latestCropPath);
        File.WriteAllText(debugInfoPath, debugText, Encoding.UTF8);

        return new OcrCaptureDebugResult(
            debugDirectory,
            fullScreenshotPath,
            annotatedScreenshotPath,
            latestCropPath,
            debugInfoPath,
            debugText);
    }

    private static DrawingBitmap CaptureScreenRect(Int32Rect rect)
    {
        var bitmap = new DrawingBitmap(Math.Max(1, rect.Width), Math.Max(1, rect.Height));
        using var graphics = DrawingGraphics.FromImage(bitmap);
        graphics.CopyFromScreen(rect.X, rect.Y, 0, 0, bitmap.Size);
        return bitmap;
    }

    private static string ResolveDebugDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "logs"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LoLChatTranslator",
                "logs")
        };

        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probePath = Path.Combine(candidate, ".write-test");
                File.WriteAllText(probePath, string.Empty);
                File.Delete(probePath);
                return candidate;
            }
            catch
            {
                // Try the next writable diagnostics location.
            }
        }

        throw new InvalidOperationException("无法创建可写入的 OCR 调试目录。");
    }

    private static void DrawCaptureRectangle(DrawingBitmap image, Int32Rect virtualRect, Int32Rect captureRect)
    {
        using var graphics = DrawingGraphics.FromImage(image);
        using var shadowPen = new DrawingPen(DrawingColor.Black, 6);
        using var highlightPen = new DrawingPen(DrawingColor.Red, 3);
        var localRect = new DrawingRectangle(
            captureRect.X - virtualRect.X,
            captureRect.Y - virtualRect.Y,
            Math.Max(1, captureRect.Width),
            Math.Max(1, captureRect.Height));

        graphics.DrawRectangle(shadowPen, localRect);
        graphics.DrawRectangle(highlightPen, localRect);
        graphics.FillRectangle(DrawingBrushes.Red, localRect.X, localRect.Y, 10, 10);
    }

    private static string BuildDebugText(
        OcrSelectionDebugInfo? selectionDebugInfo,
        (double X, double Y) dpiScale,
        Int32Rect virtualRect,
        Int32Rect monitorRect,
        Int32Rect captureRect,
        int cropWidth,
        int cropHeight,
        string fullScreenshotPath,
        string annotatedScreenshotPath,
        string latestCropPath)
    {
        var selectedRectUi = selectionDebugInfo is null
            ? "<not_available_from_saved_config>"
            : FormatRect(selectionDebugInfo.SelectedRectUi);
        var selectedRectScreen = selectionDebugInfo is null
            ? EstimateLogicalRect(captureRect, dpiScale)
            : FormatRect(selectionDebugInfo.SelectedRectScreen);
        var selectedRectPhysical = selectionDebugInfo is null
            ? FormatRect(captureRect)
            : FormatRect(selectionDebugInfo.SelectedRectPhysical);
        var windowDpiScale = selectionDebugInfo is null
            ? $"{dpiScale.X:0.###}, {dpiScale.Y:0.###}"
            : $"{selectionDebugInfo.DpiScaleX:0.###}, {selectionDebugInfo.DpiScaleY:0.###}";

        return $"""
        windows_dpi_scale={windowDpiScale}
        conversion_method=WPF logical coordinates -> PointToScreen physical pixels
        screen_logical_size={SystemParameters.VirtualScreenWidth:0.##} x {SystemParameters.VirtualScreenHeight:0.##}
        screen_physical_size={virtualRect.Width} x {virtualRect.Height}
        virtual_screen_rect={FormatRect(virtualRect)}
        monitor_rect={FormatRect(monitorRect)}
        selected_rect_ui={selectedRectUi}
        selected_rect_screen={selectedRectScreen}
        selected_rect_physical={selectedRectPhysical}
        capture_rect_used={FormatRect(captureRect)}
        crop_size={cropWidth} x {cropHeight}
        full_screenshot={fullScreenshotPath}
        full_screenshot_with_selected_rect={annotatedScreenshotPath}
        latest_crop={latestCropPath}
        """;
    }

    private static string EstimateLogicalRect(Int32Rect physicalRect, (double X, double Y) dpiScale)
    {
        var scaleX = dpiScale.X <= 0 ? 1 : dpiScale.X;
        var scaleY = dpiScale.Y <= 0 ? 1 : dpiScale.Y;
        return $"x={physicalRect.X / scaleX:0.##}, y={physicalRect.Y / scaleY:0.##}, w={physicalRect.Width / scaleX:0.##}, h={physicalRect.Height / scaleY:0.##} (estimated)";
    }

    private static Int32Rect GetVirtualScreenRect()
    {
        return new Int32Rect(
            GetSystemMetrics(SmXVirtualScreen),
            GetSystemMetrics(SmYVirtualScreen),
            Math.Max(1, GetSystemMetrics(SmCxVirtualScreen)),
            Math.Max(1, GetSystemMetrics(SmCyVirtualScreen)));
    }

    private static Int32Rect GetMonitorRect(Int32Rect rect)
    {
        var nativeRect = ToNativeRect(rect);
        var monitor = MonitorFromRect(ref nativeRect, MonitorDefaultToNearest);
        var info = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };

        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return GetVirtualScreenRect();
        }

        return FromNativeRect(info.Monitor);
    }

    private static (double X, double Y) GetMonitorDpiScale(Int32Rect rect)
    {
        try
        {
            var nativeRect = ToNativeRect(rect);
            var monitor = MonitorFromRect(ref nativeRect, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero
                && GetDpiForMonitor(monitor, 0, out var dpiX, out var dpiY) == 0)
            {
                return (dpiX / 96D, dpiY / 96D);
            }
        }
        catch
        {
            // Use a neutral scale if Windows cannot provide per-monitor DPI.
        }

        return (1, 1);
    }

    private static NativeRect ToNativeRect(Int32Rect rect)
    {
        return new NativeRect
        {
            Left = rect.X,
            Top = rect.Y,
            Right = rect.X + Math.Max(1, rect.Width),
            Bottom = rect.Y + Math.Max(1, rect.Height)
        };
    }

    private static Int32Rect FromNativeRect(NativeRect rect)
    {
        return new Int32Rect(
            rect.Left,
            rect.Top,
            Math.Max(1, rect.Right - rect.Left),
            Math.Max(1, rect.Bottom - rect.Top));
    }

    private static string FormatRect(Rect rect)
    {
        return $"x={rect.X:0.##}, y={rect.Y:0.##}, w={rect.Width:0.##}, h={rect.Height:0.##}";
    }

    private static string FormatRect(Int32Rect rect)
    {
        return $"x={rect.X}, y={rect.Y}, w={rect.Width}, h={rect.Height}";
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
