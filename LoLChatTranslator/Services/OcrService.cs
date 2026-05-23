using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel;
using LoLChatTranslator.Models;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace LoLChatTranslator.Services;

public sealed record OcrRecognitionResult(
    List<string> Lines,
    string EngineName,
    bool CaptureSucceeded,
    string? ErrorMessage,
    List<OcrTextLine>? TextLines = null,
    OcrRunDiagnostics? Diagnostics = null);

public sealed record OcrRunDiagnostics(
    long? CaptureMs = null,
    long? CropMs = null,
    long? TextMaskMs = null,
    long? DirtyDetectMs = null,
    long? PreprocessMs = null,
    long? OcrDetectMs = null,
    long? OcrRecognizeMs = null,
    long? OcrFullMs = null,
    long? OcrRecognizeLinesMs = null,
    long? OcrTotalMs = null,
    long? OcrRequestMs = null,
    long? OcrInferenceMs = null,
    long? JsonParseMs = null,
    long? PostProcessMs = null,
    long? DedupeMs = null,
    long? TranslateMs = null,
    long? OverlayMs = null,
    long? CycleTotalMs = null,
    long? TotalMs = null,
    long? ColdStartMs = null,
    long? WorkerStartMs = null,
    long? WarmRunMs = null,
    long? ModelInitMs = null,
    string Backend = "unknown",
    string Mode = "stable",
    string Task = "full",
    string PerformanceMode = "stable",
    string Parameters = "",
    bool SameAsLastCapture = false,
    bool? ImageHashChanged = null,
    bool? TextMaskChanged = null,
    string? CaptureHash = null,
    string? OcrInputImagePath = null,
    string? SelectedLanguage = null,
    string? PaddleOcrVersion = null,
    string? PaddlePaddleVersion = null,
    string? DetModelName = null,
    string? RecModelName = null,
    bool? UseSpaceChar = null,
    string? FallbackReason = null,
    string? UnsupportedParameters = null,
    bool? WorkerColdStart = null,
    bool? ModelAlreadyLoaded = null,
    int? WorkerExitCode = null,
    string? WorkerStderrTail = null,
    string? WorkerStdoutTail = null,
    string? WorkerLogPath = null,
    string? WorkerScriptPath = null,
    string? WorkerScriptLastWriteTime = null,
    string? WorkerScriptSha256 = null,
    string? SourceScriptPath = null,
    string? SourceScriptSha256 = null,
    string? LastRequestId = null,
    string? LastRequestAction = null,
    string? LastRequestImagePath = null,
    string? LastRequestMode = null,
    string? LastRequestLang = null,
    string? LastRequestTask = null,
    string? PayloadErrorKind = null,
    bool? RestartWorker = null,
    bool? UsedFullOcr = null,
    bool? UsedRecognitionOnly = null,
    bool? LocalRegionFullOcr = null,
    string? RecognitionOnlyReason = null,
    int? DirtyLineCount = null,
    int? ChangedPixels = null,
    double? DirtyRegionRatio = null,
    string? FullRescanReason = null,
    string? CropRegions = null,
    string? RawText = null,
    string? ReadingOrder = null);

public sealed record OcrEngineRunResult(
    List<OcrTextLine> Lines,
    OcrRunDiagnostics Diagnostics,
    string? ErrorMessage = null);

public sealed record OcrWarmUpResult(
    string EngineName,
    string Mode,
    string Backend,
    string Parameters,
    long? ColdStartMs,
    long? WarmRunMs,
    string? ErrorMessage);

public sealed record OcrEngineComparisonResult(
    string EngineName,
    string TestName,
    string Parameters,
    byte[] InputImagePng,
    int InputWidth,
    int InputHeight,
    List<OcrTextLine> Lines,
    string? ErrorMessage,
    OcrRunDiagnostics Diagnostics);

public sealed record OcrCapturePreviewResult(
    byte[] CapturedImagePng,
    int CapturedWidth,
    int CapturedHeight,
    bool SameAsLastCapture,
    string CaptureHash,
    long CaptureMs);

public sealed record OcrTestReport(
    OcrCapturePreviewResult CapturePreview,
    List<OcrEngineComparisonResult> EngineResults,
    long TotalMs,
    string EngineName,
    string Mode,
    OcrDirtyRegionPreviewResult? DirtyRegionPreview = null,
    bool IsPreprocessComparison = false);

public sealed record OcrDirtyRegionPreviewResult(
    byte[] TextMaskImagePng,
    byte[] DirtyMaskImagePng,
    string DirtyRegions,
    int DirtyLineCount,
    bool UsedFullOcr,
    bool UsedRecognitionOnly,
    string FullRescanReason,
    long TextMaskMs,
    long DirtyDetectMs,
    string RecognitionOnlyReason);

public interface IOcrEngine
{
    string Name { get; }

    Task<OcrEngineRunResult> RecognizeAsync(Bitmap image, OcrConfig config, CancellationToken cancellationToken = default);

    Task<OcrWarmUpResult> WarmUpAsync(OcrConfig config, CancellationToken cancellationToken = default);
}

public interface IResettableOcrEngine
{
    void ResetWorker();
}

public interface IWorkerBackedOcrEngine
{
    bool IsWorkerReady(string mode, string language);
}

public sealed class OcrWorkerProcessException : InvalidOperationException
{
    public OcrWorkerProcessException(string message, OcrRunDiagnostics diagnostics)
        : base(message)
    {
        Diagnostics = diagnostics;
    }

    public OcrRunDiagnostics Diagnostics { get; }
}

public sealed class OcrService : IDisposable
{
    private readonly Dictionary<string, IOcrEngine> _engines;
    private string? _lastCaptureHash;

    public OcrService()
    {
        var windowsOcrEngine = new WindowsOcrEngine();
        var ppOcrV5Engine = new PpOcrV5MultilingualEngine();
        _engines = new Dictionary<string, IOcrEngine>(StringComparer.OrdinalIgnoreCase)
        {
            [OcrEngines.WindowsOcr] = windowsOcrEngine,
            ["Windows OCR"] = windowsOcrEngine,
            [OcrEngines.PpOcrV5Multilingual] = ppOcrV5Engine,
            ["PP-OCRv5 多语言版"] = ppOcrV5Engine
        };
    }

    public async Task<OcrRecognitionResult> RecognizeChatLinesWithDiagnosticsAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var captureStopwatch = Stopwatch.StartNew();
        using var capturedImage = CaptureConfiguredRegion(config.OcrConfig);
        captureStopwatch.Stop();
        if (capturedImage is null)
        {
            var engine = ResolveEngine(config.OcrConfig.OcrEngine);
            return new OcrRecognitionResult(
                [],
                engine.Name,
                CaptureSucceeded: false,
                "截图失败：请检查框选范围是否在屏幕内。",
                Diagnostics: new OcrRunDiagnostics(CaptureMs: captureStopwatch.ElapsedMilliseconds, TotalMs: totalStopwatch.ElapsedMilliseconds));
        }

        var result = await RecognizeChatLinesWithDiagnosticsAsync(config, capturedImage, captureStopwatch.ElapsedMilliseconds, cancellationToken);
        totalStopwatch.Stop();
        return result with
        {
            Diagnostics = MergeDiagnostics(result.Diagnostics, new OcrRunDiagnostics(
                CaptureMs: captureStopwatch.ElapsedMilliseconds,
                TotalMs: totalStopwatch.ElapsedMilliseconds))
        };
    }

    public async Task<OcrRecognitionResult> RecognizeChatLinesWithDiagnosticsAsync(AppConfig config, Bitmap capturedImage, CancellationToken cancellationToken = default)
    {
        return await RecognizeChatLinesWithDiagnosticsAsync(config, capturedImage, null, cancellationToken);
    }

    private async Task<OcrRecognitionResult> RecognizeChatLinesWithDiagnosticsAsync(AppConfig config, Bitmap capturedImage, long? captureMs, CancellationToken cancellationToken)
    {
        var engine = ResolveEngine(config.OcrConfig.OcrEngine);
        var totalStopwatch = Stopwatch.StartNew();
        var captureHash = ComputeImageFingerprint(capturedImage);
        var sameAsLastCapture = string.Equals(captureHash, _lastCaptureHash, StringComparison.Ordinal);
        _lastCaptureHash = captureHash;

        try
        {
            var preprocessStopwatch = Stopwatch.StartNew();
            Bitmap? processedImageToDispose = null;
            var processedImage = capturedImage;
            if (ImagePreprocessor.RequiresProcessing(config.OcrConfig))
            {
                processedImageToDispose = ImagePreprocessor.Process(capturedImage, config.OcrConfig);
                processedImage = processedImageToDispose;
            }

            preprocessStopwatch.Stop();
            var ocrInputImagePath = config.OcrConfig.SaveOcrDebugImages
                ? OcrDebugImageService.TrySaveOcrInputImage(processedImage)
                : null;

            try
            {
                var engineResult = await engine.RecognizeAsync(processedImage, config.OcrConfig, cancellationToken);
                var filteredLines = FilterByConfidence(engineResult.Lines, config.OcrConfig.MinConfidence);
                var textLines = filteredLines
                    .Select(line => line.Text.Trim())
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList();
                totalStopwatch.Stop();

                var diagnostics = MergeDiagnostics(engineResult.Diagnostics, new OcrRunDiagnostics(
                    CaptureMs: captureMs,
                    PreprocessMs: preprocessStopwatch.ElapsedMilliseconds,
                    TotalMs: totalStopwatch.ElapsedMilliseconds,
                    SameAsLastCapture: sameAsLastCapture,
                    CaptureHash: captureHash,
                    OcrInputImagePath: ocrInputImagePath));

                return new OcrRecognitionResult(textLines, engine.Name, CaptureSucceeded: true, engineResult.ErrorMessage, filteredLines, diagnostics);
            }
            finally
            {
                processedImageToDispose?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"OCR failed: {ex}");
            totalStopwatch.Stop();
            var workerDiagnostics = ex is OcrWorkerProcessException workerException
                ? workerException.Diagnostics
                : null;
            return new OcrRecognitionResult(
                [],
                engine.Name,
                CaptureSucceeded: true,
                ex.Message,
                Diagnostics: MergeDiagnostics(
                    workerDiagnostics,
                    new OcrRunDiagnostics(
                        CaptureMs: captureMs,
                        TotalMs: totalStopwatch.ElapsedMilliseconds,
                        SameAsLastCapture: sameAsLastCapture,
                        CaptureHash: captureHash,
                        Mode: OcrMode.Normalize(config.OcrConfig.OcrMode),
                        SelectedLanguage: OcrLanguages.Normalize(config.OcrConfig.OcrLanguage),
                        OcrInputImagePath: OcrDebugImageService.TrySaveOcrInputImage(capturedImage, "debug_ocr_exception_input.png"))));
        }
    }

    public async Task<OcrTestReport> CompareEnginesAsync(AppConfig config, bool runAllPreprocessComparisons = false)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var captureStopwatch = Stopwatch.StartNew();
        using var capturedImage = CaptureConfiguredRegion(config.OcrConfig)
            ?? throw new InvalidOperationException("截图失败：请检查框选范围是否在屏幕内。");
        captureStopwatch.Stop();

        var captureHash = ComputeImageFingerprint(capturedImage);
        var sameAsLastCapture = string.Equals(captureHash, _lastCaptureHash, StringComparison.Ordinal);
        _lastCaptureHash = captureHash;

        var capturePreview = new OcrCapturePreviewResult(
            EncodePng(capturedImage),
            capturedImage.Width,
            capturedImage.Height,
            sameAsLastCapture,
            captureHash,
            captureStopwatch.ElapsedMilliseconds);

        var engine = ResolveEngine(config.OcrConfig.OcrEngine);
        var dirtyPreview = BuildDirtyRegionPreview(capturedImage);
        var results = new List<OcrEngineComparisonResult>();
        var plans = runAllPreprocessComparisons
            ? ImagePreprocessor.CreateOcrTestPlans(config.OcrConfig)
            : [ImagePreprocessor.CreateConfiguredOcrTestPlan(config.OcrConfig)];

        foreach (var plan in plans)
        {
            try
            {
                var preprocessStopwatch = Stopwatch.StartNew();
                Bitmap? engineImageToDispose = null;
                var engineImage = capturedImage;
                if (ImagePreprocessor.RequiresProcessing(plan))
                {
                    engineImageToDispose = ImagePreprocessor.Process(capturedImage, plan);
                    engineImage = engineImageToDispose;
                }

                preprocessStopwatch.Stop();
                var ocrInputImagePath = OcrDebugImageService.TrySaveOcrInputImage(
                    engineImage,
                    $"debug_ocr_input_{BuildDebugFileName(plan.Name)}.png");

                try
                {
                    var run = await engine.RecognizeAsync(engineImage, config.OcrConfig, CancellationToken.None);
                    var filteredLines = FilterByConfidence(run.Lines, config.OcrConfig.MinConfidence);
                    var diagnostics = MergeDiagnostics(run.Diagnostics, new OcrRunDiagnostics(
                        CaptureMs: captureStopwatch.ElapsedMilliseconds,
                        CropMs: 0,
                        TextMaskMs: dirtyPreview?.TextMaskMs,
                        DirtyDetectMs: dirtyPreview?.DirtyDetectMs,
                        PreprocessMs: preprocessStopwatch.ElapsedMilliseconds,
                        TotalMs: captureStopwatch.ElapsedMilliseconds + preprocessStopwatch.ElapsedMilliseconds + (run.Diagnostics.OcrTotalMs ?? 0),
                        SameAsLastCapture: sameAsLastCapture,
                        CaptureHash: captureHash,
                        UsedFullOcr: true,
                        UsedRecognitionOnly: false,
                        LocalRegionFullOcr: false,
                        RecognitionOnlyReason: dirtyPreview?.RecognitionOnlyReason,
                        DirtyLineCount: dirtyPreview?.DirtyLineCount,
                        FullRescanReason: dirtyPreview?.FullRescanReason,
                        CropRegions: dirtyPreview?.DirtyRegions,
                        OcrInputImagePath: ocrInputImagePath));

                    results.Add(new OcrEngineComparisonResult(
                        engine.Name,
                        plan.Name,
                        plan.Parameters,
                        EncodePng(engineImage),
                        engineImage.Width,
                        engineImage.Height,
                        filteredLines,
                        run.ErrorMessage,
                        diagnostics));
                }
                finally
                {
                    engineImageToDispose?.Dispose();
                }
            }
            catch (Exception ex)
            {
                var workerDiagnostics = ex is OcrWorkerProcessException workerException
                    ? workerException.Diagnostics
                    : null;
                results.Add(new OcrEngineComparisonResult(
                    engine.Name,
                    plan.Name,
                    plan.Parameters,
                    EncodePng(capturedImage),
                    capturedImage.Width,
                    capturedImage.Height,
                    [],
                    ex.Message,
                    MergeDiagnostics(
                        workerDiagnostics,
                        new OcrRunDiagnostics(
                            CaptureMs: captureStopwatch.ElapsedMilliseconds,
                            CropMs: 0,
                            TextMaskMs: dirtyPreview?.TextMaskMs,
                            DirtyDetectMs: dirtyPreview?.DirtyDetectMs,
                            SameAsLastCapture: sameAsLastCapture,
                            CaptureHash: captureHash,
                            Mode: OcrMode.Normalize(config.OcrConfig.OcrMode),
                            SelectedLanguage: OcrLanguages.Normalize(config.OcrConfig.OcrLanguage),
                            UsedFullOcr: true,
                            UsedRecognitionOnly: false,
                            LocalRegionFullOcr: false,
                            RecognitionOnlyReason: dirtyPreview?.RecognitionOnlyReason,
                            DirtyLineCount: dirtyPreview?.DirtyLineCount,
                            FullRescanReason: dirtyPreview?.FullRescanReason,
                            CropRegions: dirtyPreview?.DirtyRegions,
                            OcrInputImagePath: OcrDebugImageService.TrySaveOcrInputImage(capturedImage)))));
            }
        }

        totalStopwatch.Stop();
        return new OcrTestReport(
            capturePreview,
            results,
            totalStopwatch.ElapsedMilliseconds,
            engine.Name,
            OcrMode.Normalize(config.OcrConfig.OcrMode),
            dirtyPreview,
            runAllPreprocessComparisons);
    }

    public async Task<OcrWarmUpResult> WarmUpAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        var engine = ResolveEngine(config.OcrConfig.OcrEngine);
        try
        {
            return await engine.WarmUpAsync(config.OcrConfig, cancellationToken);
        }
        catch (Exception ex)
        {
            return new OcrWarmUpResult(engine.Name, OcrMode.Normalize(config.OcrConfig.OcrMode), "unknown", "", null, null, ex.Message);
        }
    }

    public void ResetWorkers()
    {
        _lastCaptureHash = null;
        foreach (var engine in _engines.Values.Distinct())
        {
            if (engine is not IResettableOcrEngine resettable)
            {
                continue;
            }

            try
            {
                resettable.ResetWorker();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to reset OCR worker {engine.Name}: {ex.Message}");
            }
        }
    }

    public bool IsWorkerReady(AppConfig config)
    {
        var engine = ResolveEngine(config.OcrConfig.OcrEngine);
        return engine is not IWorkerBackedOcrEngine workerBacked
            || workerBacked.IsWorkerReady(
                OcrMode.Normalize(config.OcrConfig.OcrMode),
                OcrLanguages.Normalize(config.OcrConfig.OcrLanguage));
    }

    public void Dispose()
    {
        foreach (var engine in _engines.Values.Distinct())
        {
            if (engine is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private IOcrEngine ResolveEngine(string engineName)
    {
        var normalized = OcrEngines.Normalize(engineName);
        return _engines.TryGetValue(normalized, out var engine)
            ? engine
            : _engines[OcrEngines.PpOcrV5Multilingual];
    }

    private static List<OcrTextLine> FilterByConfidence(IEnumerable<OcrTextLine> lines, double minConfidence)
    {
        var threshold = Math.Clamp(minConfidence, 0, 1);
        return lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .Where(line => line.Confidence <= 0 || line.Confidence >= threshold)
            .ToList();
    }

    private static byte[] EncodePng(Bitmap image)
    {
        using var memoryStream = new MemoryStream();
        image.Save(memoryStream, ImageFormat.Png);
        return memoryStream.ToArray();
    }

    private static string BuildDebugFileName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        var fileName = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(fileName) ? "ocr_input" : fileName;
    }

    private static OcrDirtyRegionPreviewResult? BuildDirtyRegionPreview(Bitmap capturedImage)
    {
        try
        {
            var maskStopwatch = Stopwatch.StartNew();
            using var frame = TextMaskFrame.Create(capturedImage);
            using var mask = frame.CreateMaskPreview();
            maskStopwatch.Stop();

            return new OcrDirtyRegionPreviewResult(
                EncodePng(mask),
                EncodePng(mask),
                $"0,0,{capturedImage.Width},{capturedImage.Height}",
                frame.Lines.Count,
                UsedFullOcr: true,
                UsedRecognitionOnly: false,
                FullRescanReason: "test_window_initial_full_scan",
                TextMaskMs: maskStopwatch.ElapsedMilliseconds,
                DirtyDetectMs: 0,
                RecognitionOnlyReason: "api_not_supported_local_region_full_ocr_fallback");
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Failed to build OCR dirty region preview: {ex.Message}");
            return null;
        }
    }

    public static string ComputeImageFingerprint(Bitmap image)
    {
        using var normalized = image.PixelFormat == PixelFormat.Format32bppArgb
            ? null
            : image.Clone(new Rectangle(0, 0, image.Width, image.Height), PixelFormat.Format32bppArgb);
        var imageToHash = normalized ?? image;

        var bounds = new Rectangle(0, 0, imageToHash.Width, imageToHash.Height);
        var data = imageToHash.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unchecked
            {
                var hash = 14695981039346656037UL;
                hash = (hash ^ (uint)imageToHash.Width) * 1099511628211UL;
                hash = (hash ^ (uint)imageToHash.Height) * 1099511628211UL;

                var rowLength = Math.Abs(data.Stride);
                var row = new byte[rowLength];
                for (var y = 0; y < imageToHash.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, rowLength);
                    for (var i = 0; i < rowLength; i++)
                    {
                        hash ^= row[i];
                        hash *= 1099511628211UL;
                    }
                }

                return hash.ToString("X16");
            }
        }
        finally
        {
            imageToHash.UnlockBits(data);
        }
    }

    private static OcrRunDiagnostics MergeDiagnostics(OcrRunDiagnostics? primary, OcrRunDiagnostics secondary)
    {
        primary ??= new OcrRunDiagnostics();

        return primary with
        {
            CaptureMs = primary.CaptureMs ?? secondary.CaptureMs,
            CropMs = primary.CropMs ?? secondary.CropMs,
            TextMaskMs = primary.TextMaskMs ?? secondary.TextMaskMs,
            DirtyDetectMs = primary.DirtyDetectMs ?? secondary.DirtyDetectMs,
            PreprocessMs = primary.PreprocessMs ?? secondary.PreprocessMs,
            OcrDetectMs = primary.OcrDetectMs ?? secondary.OcrDetectMs,
            OcrRecognizeMs = primary.OcrRecognizeMs ?? secondary.OcrRecognizeMs,
            OcrFullMs = primary.OcrFullMs ?? secondary.OcrFullMs,
            OcrRecognizeLinesMs = primary.OcrRecognizeLinesMs ?? secondary.OcrRecognizeLinesMs,
            OcrTotalMs = primary.OcrTotalMs ?? secondary.OcrTotalMs,
            OcrRequestMs = primary.OcrRequestMs ?? secondary.OcrRequestMs,
            OcrInferenceMs = primary.OcrInferenceMs ?? secondary.OcrInferenceMs,
            JsonParseMs = primary.JsonParseMs ?? secondary.JsonParseMs,
            PostProcessMs = primary.PostProcessMs ?? secondary.PostProcessMs,
            DedupeMs = primary.DedupeMs ?? secondary.DedupeMs,
            TranslateMs = primary.TranslateMs ?? secondary.TranslateMs,
            OverlayMs = primary.OverlayMs ?? secondary.OverlayMs,
            CycleTotalMs = primary.CycleTotalMs ?? secondary.CycleTotalMs,
            TotalMs = secondary.TotalMs ?? primary.TotalMs,
            ColdStartMs = primary.ColdStartMs ?? secondary.ColdStartMs,
            WorkerStartMs = primary.WorkerStartMs ?? secondary.WorkerStartMs,
            WarmRunMs = primary.WarmRunMs ?? secondary.WarmRunMs,
            ModelInitMs = primary.ModelInitMs ?? secondary.ModelInitMs,
            Backend = primary.Backend != "unknown" ? primary.Backend : secondary.Backend,
            Mode = primary.Mode != "stable" ? primary.Mode : secondary.Mode,
            Task = primary.Task != "full" ? primary.Task : secondary.Task,
            PerformanceMode = primary.PerformanceMode != "stable" ? primary.PerformanceMode : secondary.PerformanceMode,
            Parameters = !string.IsNullOrWhiteSpace(primary.Parameters) ? primary.Parameters : secondary.Parameters,
            SameAsLastCapture = primary.SameAsLastCapture || secondary.SameAsLastCapture,
            ImageHashChanged = primary.ImageHashChanged ?? secondary.ImageHashChanged,
            TextMaskChanged = primary.TextMaskChanged ?? secondary.TextMaskChanged,
            CaptureHash = primary.CaptureHash ?? secondary.CaptureHash,
            OcrInputImagePath = primary.OcrInputImagePath ?? secondary.OcrInputImagePath,
            SelectedLanguage = primary.SelectedLanguage ?? secondary.SelectedLanguage,
            PaddleOcrVersion = primary.PaddleOcrVersion ?? secondary.PaddleOcrVersion,
            PaddlePaddleVersion = primary.PaddlePaddleVersion ?? secondary.PaddlePaddleVersion,
            DetModelName = primary.DetModelName ?? secondary.DetModelName,
            RecModelName = primary.RecModelName ?? secondary.RecModelName,
            UseSpaceChar = primary.UseSpaceChar ?? secondary.UseSpaceChar,
            FallbackReason = primary.FallbackReason ?? secondary.FallbackReason,
            UnsupportedParameters = primary.UnsupportedParameters ?? secondary.UnsupportedParameters,
            WorkerColdStart = primary.WorkerColdStart ?? secondary.WorkerColdStart,
            ModelAlreadyLoaded = primary.ModelAlreadyLoaded ?? secondary.ModelAlreadyLoaded,
            WorkerExitCode = primary.WorkerExitCode ?? secondary.WorkerExitCode,
            WorkerStderrTail = primary.WorkerStderrTail ?? secondary.WorkerStderrTail,
            WorkerStdoutTail = primary.WorkerStdoutTail ?? secondary.WorkerStdoutTail,
            WorkerLogPath = primary.WorkerLogPath ?? secondary.WorkerLogPath,
            WorkerScriptPath = primary.WorkerScriptPath ?? secondary.WorkerScriptPath,
            WorkerScriptLastWriteTime = primary.WorkerScriptLastWriteTime ?? secondary.WorkerScriptLastWriteTime,
            WorkerScriptSha256 = primary.WorkerScriptSha256 ?? secondary.WorkerScriptSha256,
            SourceScriptPath = primary.SourceScriptPath ?? secondary.SourceScriptPath,
            SourceScriptSha256 = primary.SourceScriptSha256 ?? secondary.SourceScriptSha256,
            LastRequestId = primary.LastRequestId ?? secondary.LastRequestId,
            LastRequestAction = primary.LastRequestAction ?? secondary.LastRequestAction,
            LastRequestImagePath = primary.LastRequestImagePath ?? secondary.LastRequestImagePath,
            LastRequestMode = primary.LastRequestMode ?? secondary.LastRequestMode,
            LastRequestLang = primary.LastRequestLang ?? secondary.LastRequestLang,
            LastRequestTask = primary.LastRequestTask ?? secondary.LastRequestTask,
            PayloadErrorKind = primary.PayloadErrorKind ?? secondary.PayloadErrorKind,
            RestartWorker = primary.RestartWorker ?? secondary.RestartWorker,
            UsedFullOcr = primary.UsedFullOcr ?? secondary.UsedFullOcr,
            UsedRecognitionOnly = primary.UsedRecognitionOnly ?? secondary.UsedRecognitionOnly,
            LocalRegionFullOcr = primary.LocalRegionFullOcr ?? secondary.LocalRegionFullOcr,
            RecognitionOnlyReason = primary.RecognitionOnlyReason ?? secondary.RecognitionOnlyReason,
            DirtyLineCount = primary.DirtyLineCount ?? secondary.DirtyLineCount,
            ChangedPixels = primary.ChangedPixels ?? secondary.ChangedPixels,
            DirtyRegionRatio = primary.DirtyRegionRatio ?? secondary.DirtyRegionRatio,
            FullRescanReason = primary.FullRescanReason ?? secondary.FullRescanReason,
            CropRegions = primary.CropRegions ?? secondary.CropRegions,
            RawText = primary.RawText ?? secondary.RawText,
            ReadingOrder = primary.ReadingOrder ?? secondary.ReadingOrder
        };
    }

    public static Bitmap? CaptureConfiguredRegion(OcrConfig config)
    {
        try
        {
            var width = Math.Max(1, config.RegionWidth);
            var height = Math.Max(1, config.RegionHeight);
            var bitmap = new Bitmap(width, height);

            using var graphics = Graphics.FromImage(bitmap);
            // Safe screen capture only. This does not hook, inject, read memory, or touch the game process.
            graphics.CopyFromScreen(config.RegionX, config.RegionY, 0, 0, bitmap.Size);

            return bitmap;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Screen region capture failed: {ex}");
            return null;
        }
    }

}

public static class ImagePreprocessor
{
    public sealed record Plan(
        string Name,
        double ImageScale,
        double Contrast,
        bool EnableSharpen,
        bool Grayscale,
        bool Binarize,
        string Parameters);

    public static Bitmap Process(Bitmap source, OcrConfig config)
    {
        return Process(source, new Plan(
            "configured",
            Math.Clamp(config.ImageScale, 1, 4),
            Math.Clamp(config.Contrast, 0.5, 3.0),
            config.EnableSharpen,
            Grayscale: false,
            Binarize: false,
            $"scale={Math.Clamp(config.ImageScale, 1, 4):0.##}; contrast={Math.Clamp(config.Contrast, 0.5, 3.0):0.##}; sharpen={config.EnableSharpen}; mode={OcrMode.Normalize(config.OcrMode)}"));
    }

    public static bool RequiresProcessing(OcrConfig config)
    {
        return Math.Abs(Math.Clamp(config.ImageScale, 1, 4) - 1) >= 0.001
            || Math.Abs(Math.Clamp(config.Contrast, 0.5, 3.0) - 1) >= 0.001
            || config.EnableSharpen;
    }

    public static bool RequiresProcessing(Plan plan)
    {
        return Math.Abs(Math.Clamp(plan.ImageScale, 1, 4) - 1) >= 0.001
            || Math.Abs(Math.Clamp(plan.Contrast, 0.5, 3.0) - 1) >= 0.001
            || plan.EnableSharpen
            || plan.Grayscale
            || plan.Binarize;
    }

    public static IReadOnlyList<Plan> CreateOcrTestPlans(OcrConfig config)
    {
        return
        [
            CreateConfiguredOcrTestPlan(config),
            new Plan("原图 OCR", 1, 1, false, false, false, "original; scale=1; no preprocess"),
            new Plan("灰度 OCR", 1, 1, false, true, false, "grayscale; scale=1"),
            new Plan("对比度增强 OCR", 1, 1.5, false, false, false, "contrast=1.5; scale=1"),
            new Plan("二值化 OCR", 1, 1, false, true, true, "otsu_binary; scale=1")
        ];
    }

    public static Plan CreateConfiguredOcrTestPlan(OcrConfig config)
    {
        return new Plan(
            "当前配置 / 原图 OCR",
            Math.Clamp(config.ImageScale, 1, 4),
            Math.Clamp(config.Contrast, 0.5, 3),
            config.EnableSharpen,
            false,
            false,
            $"configured; scale={Math.Clamp(config.ImageScale, 1, 4):0.##}; contrast={Math.Clamp(config.Contrast, 0.5, 3):0.##}; sharpen={config.EnableSharpen}; single=true");
    }

    public static Bitmap Process(Bitmap source, Plan plan)
    {
        var scale = Math.Clamp(plan.ImageScale, 1, 4);
        Bitmap current;
        if (Math.Abs(scale - 1) < 0.001)
        {
            current = (Bitmap)source.Clone();
        }
        else
        {
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));

            current = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(current))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            }
        }

        if (plan.Grayscale)
        {
            using var previous = current;
            current = ApplyGrayscale(previous);
        }

        if (plan.Binarize)
        {
            using var previous = current;
            current = ApplyOtsuBinarization(previous);
        }

        if (Math.Abs(plan.Contrast - 1) >= 0.001)
        {
            using var previous = current;
            current = ApplyContrast(previous, Math.Clamp(plan.Contrast, 0.5, 3.0));
        }

        if (plan.EnableSharpen)
        {
            using var previous = current;
            current = ApplySharpen(previous);
        }

        return current;
    }

    private static Bitmap ApplyGrayscale(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        var bounds = new Rectangle(0, 0, source.Width, source.Height);
        using var normalized = source.Clone(bounds, PixelFormat.Format32bppArgb);
        var sourceData = normalized.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var resultData = result.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowLength = Math.Abs(sourceData.Stride);
            var row = new byte[rowLength];
            for (var y = 0; y < source.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(sourceData.Scan0, y * sourceData.Stride), row, 0, rowLength);
                for (var x = 0; x < source.Width; x++)
                {
                    var index = x * 4;
                    var gray = (byte)((row[index + 2] * 30 + row[index + 1] * 59 + row[index] * 11) / 100);
                    row[index] = gray;
                    row[index + 1] = gray;
                    row[index + 2] = gray;
                    row[index + 3] = 255;
                }

                Marshal.Copy(row, 0, IntPtr.Add(resultData.Scan0, y * resultData.Stride), rowLength);
            }
        }
        finally
        {
            normalized.UnlockBits(sourceData);
            result.UnlockBits(resultData);
        }

        return result;
    }

    private static Bitmap ApplyOtsuBinarization(Bitmap source)
    {
        var width = source.Width;
        var height = source.Height;
        var bounds = new Rectangle(0, 0, width, height);
        var grayValues = new byte[width * height];
        var histogram = new int[256];

        using (var normalized = source.Clone(bounds, PixelFormat.Format32bppArgb))
        {
            var sourceData = normalized.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var rowLength = Math.Abs(sourceData.Stride);
                var row = new byte[rowLength];
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(sourceData.Scan0, y * sourceData.Stride), row, 0, rowLength);
                    var grayRow = y * width;
                    for (var x = 0; x < width; x++)
                    {
                        var index = x * 4;
                        var gray = (byte)((row[index + 2] * 30 + row[index + 1] * 59 + row[index] * 11) / 100);
                        grayValues[grayRow + x] = gray;
                        histogram[gray]++;
                    }
                }
            }
            finally
            {
                normalized.UnlockBits(sourceData);
            }
        }

        var threshold = ComputeOtsuThreshold(histogram, width * height);
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var resultData = result.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowLength = Math.Abs(resultData.Stride);
            var row = new byte[rowLength];
            for (var y = 0; y < height; y++)
            {
                Array.Clear(row);
                var grayRow = y * width;
                for (var x = 0; x < width; x++)
                {
                    var value = grayValues[grayRow + x] >= threshold ? (byte)255 : (byte)0;
                    var index = x * 4;
                    row[index] = value;
                    row[index + 1] = value;
                    row[index + 2] = value;
                    row[index + 3] = 255;
                }

                Marshal.Copy(row, 0, IntPtr.Add(resultData.Scan0, y * resultData.Stride), rowLength);
            }
        }
        finally
        {
            result.UnlockBits(resultData);
        }

        return result;
    }

    private static int ComputeOtsuThreshold(int[] histogram, int total)
    {
        var sum = 0D;
        for (var i = 0; i < histogram.Length; i++)
        {
            sum += i * histogram[i];
        }

        var sumBackground = 0D;
        var weightBackground = 0;
        var maxVariance = 0D;
        var threshold = 128;

        for (var i = 0; i < histogram.Length; i++)
        {
            weightBackground += histogram[i];
            if (weightBackground == 0)
            {
                continue;
            }

            var weightForeground = total - weightBackground;
            if (weightForeground == 0)
            {
                break;
            }

            sumBackground += i * histogram[i];
            var meanBackground = sumBackground / weightBackground;
            var meanForeground = (sum - sumBackground) / weightForeground;
            var variance = weightBackground * weightForeground * Math.Pow(meanBackground - meanForeground, 2);
            if (variance > maxVariance)
            {
                maxVariance = variance;
                threshold = i;
            }
        }

        return threshold;
    }

    private static Bitmap ApplyContrast(Bitmap source, double contrast)
    {
        if (Math.Abs(contrast - 1) < 0.001)
        {
            return (Bitmap)source.Clone();
        }

        var result = new Bitmap(source.Width, source.Height);
        var factor = (float)contrast;
        var translate = 0.5f * (1f - factor);
        var colorMatrix = new ColorMatrix(
        [
            [factor, 0, 0, 0, 0],
            [0, factor, 0, 0, 0],
            [0, 0, factor, 0, 0],
            [0, 0, 0, 1, 0],
            [translate, translate, translate, 0, 1]
        ]);

        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(colorMatrix);

        using var graphics = Graphics.FromImage(result);
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, source.Width, source.Height),
            0,
            0,
            source.Width,
            source.Height,
            GraphicsUnit.Pixel,
            attributes);

        return result;
    }

    private static Bitmap ApplySharpen(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.DrawImageUnscaled(source, 0, 0);

        if (source.Width < 3 || source.Height < 3)
        {
            return result;
        }

        var bounds = new Rectangle(0, 0, result.Width, result.Height);
        var bitmapData = result.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var stride = bitmapData.Stride;
            var rowLength = Math.Abs(stride);
            var byteCount = rowLength * result.Height;
            var pixels = new byte[byteCount];
            for (var y = 0; y < result.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(bitmapData.Scan0, y * stride), pixels, y * rowLength, rowLength);
            }

            var sharpened = (byte[])pixels.Clone();
            for (var y = 1; y < result.Height - 1; y++)
            {
                var row = y * rowLength;
                var previousRow = (y - 1) * rowLength;
                var nextRow = (y + 1) * rowLength;

                for (var x = 1; x < result.Width - 1; x++)
                {
                    var index = row + (x * 4);
                    var left = index - 4;
                    var right = index + 4;
                    var top = previousRow + (x * 4);
                    var bottom = nextRow + (x * 4);

                    sharpened[index] = ClampByte((pixels[index] * 5) - pixels[left] - pixels[right] - pixels[top] - pixels[bottom]);
                    sharpened[index + 1] = ClampByte((pixels[index + 1] * 5) - pixels[left + 1] - pixels[right + 1] - pixels[top + 1] - pixels[bottom + 1]);
                    sharpened[index + 2] = ClampByte((pixels[index + 2] * 5) - pixels[left + 2] - pixels[right + 2] - pixels[top + 2] - pixels[bottom + 2]);
                }
            }

            for (var y = 0; y < result.Height; y++)
            {
                Marshal.Copy(sharpened, y * rowLength, IntPtr.Add(bitmapData.Scan0, y * stride), rowLength);
            }
        }
        finally
        {
            result.UnlockBits(bitmapData);
        }

        return result;
    }

    private static byte ClampByte(int value)
    {
        return (byte)Math.Clamp(value, 0, 255);
    }
}

public sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly Lazy<OcrEngine?> _ocrEngine = new(CreateOcrEngine);

    public string Name => "WindowsOCR";

    public async Task<OcrEngineRunResult> RecognizeAsync(Bitmap image, OcrConfig config, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ocrEngine = _ocrEngine.Value;
        if (ocrEngine is null)
        {
            throw new InvalidOperationException("Windows OCR 不可用：请在 Windows 设置中安装英文 OCR/语言功能。");
        }

        var stopwatch = Stopwatch.StartNew();
        using var softwareBitmap = await ConvertToSoftwareBitmapAsync(image);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await ocrEngine.RecognizeAsync(softwareBitmap);
        cancellationToken.ThrowIfCancellationRequested();
        stopwatch.Stop();

        var lines = result.Lines
            .Select(line => new OcrTextLine
            {
                Text = line.Text?.Trim() ?? string.Empty,
                Confidence = 1,
                BoundingBox = null
            })
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .ToList();

        return new OcrEngineRunResult(
            lines,
            new OcrRunDiagnostics(
                OcrRecognizeMs: stopwatch.ElapsedMilliseconds,
                OcrTotalMs: stopwatch.ElapsedMilliseconds,
                WarmRunMs: stopwatch.ElapsedMilliseconds,
                Backend: "Windows.Media.Ocr",
                Mode: OcrMode.Normalize(config.OcrMode),
                SelectedLanguage: "WindowsOCR",
                RawText: string.Join(Environment.NewLine, lines.Select(line => line.Text)),
                Parameters: "cached_engine=true; horizontal_text=true; angle_classifier=false"));
    }

    public async Task<OcrWarmUpResult> WarmUpAsync(OcrConfig config, CancellationToken cancellationToken = default)
    {
        var coldStart = Stopwatch.StartNew();
        var ocrEngine = _ocrEngine.Value;
        coldStart.Stop();
        if (ocrEngine is null)
        {
            return new OcrWarmUpResult(Name, OcrMode.Normalize(config.OcrMode), "Windows.Media.Ocr", "", null, null, "Windows OCR 不可用。");
        }

        using var image = new Bitmap(8, 8);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
        }

        var warm = Stopwatch.StartNew();
        await RecognizeAsync(image, config, cancellationToken);
        warm.Stop();

        return new OcrWarmUpResult(
            Name,
            OcrMode.Normalize(config.OcrMode),
            "Windows.Media.Ocr",
            "cached_engine=true; horizontal_text=true; angle_classifier=false",
            coldStart.ElapsedMilliseconds,
            warm.ElapsedMilliseconds,
            null);
    }

    private static OcrEngine? CreateOcrEngine()
    {
        var english = new Language("en-US");
        if (OcrEngine.IsLanguageSupported(english))
        {
            return OcrEngine.TryCreateFromLanguage(english);
        }

        return OcrEngine.TryCreateFromUserProfileLanguages();
    }

    private static async Task<SoftwareBitmap> ConvertToSoftwareBitmapAsync(Bitmap image)
    {
        using var memoryStream = new MemoryStream();
        image.Save(memoryStream, ImageFormat.Png);
        var bytes = memoryStream.ToArray();

        using var randomAccessStream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(randomAccessStream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync().AsTask();
            await writer.FlushAsync().AsTask();
            writer.DetachStream();
        }

        randomAccessStream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }
}

public sealed class PpOcrV5MultilingualEngine : LocalPythonOcrEngine
{
    public PpOcrV5MultilingualEngine()
        : base(OcrEngines.PpOcrV5Multilingual, "ppocrv5_multilingual.py")
    {
    }
}

public abstract class LocalPythonOcrEngine : IOcrEngine, IResettableOcrEngine, IWorkerBackedOcrEngine, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly TimeSpan WorkerResponseTimeout = TimeSpan.FromSeconds(30);

    private readonly string _scriptName;
    private readonly SemaphoreSlim _workerLock = new(1, 1);
    private readonly object _workerRestartSync = new();
    private readonly ConcurrentQueue<string> _workerErrors = new();
    private readonly ConcurrentQueue<string> _workerStdoutNonJson = new();
    private Process? _workerProcess;
    private StreamWriter? _workerInput;
    private StreamReader? _workerOutput;
    private string? _workerMode;
    private string? _workerLanguage;
    private string _backend = "unknown";
    private string _parameters = "";
    private long? _pendingColdStartMs;
    private long? _modelInitMs;
    private long? _lastWorkerStartMs;
    private long? _lastRequestMs;
    private long? _lastJsonParseMs;
    private bool _lastWorkerColdStart;
    private bool _lastModelAlreadyLoaded;
    private string? _pendingResetReason;
    private LocalOcrRequest? _lastRequest;

    protected LocalPythonOcrEngine(string name, string scriptName)
    {
        Name = name;
        _scriptName = scriptName;
    }

    public string Name { get; }

    public async Task<OcrEngineRunResult> RecognizeAsync(Bitmap image, OcrConfig config, CancellationToken cancellationToken = default)
    {
        var tempImagePath = Path.Combine(Path.GetTempPath(), $"lolchat-ocr-{Guid.NewGuid():N}.png");
        try
        {
            image.Save(tempImagePath, ImageFormat.Png);

            var mode = OcrMode.Normalize(config.OcrMode);
            var language = OcrLanguages.Normalize(config.OcrLanguage);
            await WaitForWorkerLockAsync("recognize", cancellationToken);
            try
            {
                ApplyPendingRestartIfNeeded();
                await EnsureWorkerAsync(mode, language, config, cancellationToken);
                var payload = await SendWorkerRequestAsync("recognize", tempImagePath, mode, language, cancellationToken);
                if (!string.IsNullOrWhiteSpace(payload.Error))
                {
                    var errorKind = NormalizePayloadErrorKind(payload.ErrorKind);
                    var shouldRestart = ShouldRestartWorkerForPayloadError(errorKind);
                    WriteWorkerDebug($"{Name} payload_error request_id={payload.RequestId ?? "<none>"} error_kind={errorKind} restart_worker={shouldRestart.ToString().ToLowerInvariant()} error={CleanWorkerLog(payload.Error)}");
                    var errorDiagnostics = BuildDiagnostics(payload, mode, consumeColdStart: true) with
                    {
                        PayloadErrorKind = errorKind,
                        RestartWorker = shouldRestart
                    };
                    if (shouldRestart)
                    {
                        RestartWorker($"payload_error:{errorKind}");
                    }

                    return new OcrEngineRunResult([], errorDiagnostics, payload.Error);
                }

                var successDiagnostics = BuildDiagnostics(payload, mode, consumeColdStart: true);
                return new OcrEngineRunResult(ParseLines(payload), successDiagnostics);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RestartWorker("recognize_cancelled");
                throw;
            }
            finally
            {
                ApplyPendingRestartIfNeeded();
                _workerLock.Release();
            }
        }
        finally
        {
            DeleteTempFile(tempImagePath);
        }
    }

    public async Task<OcrWarmUpResult> WarmUpAsync(OcrConfig config, CancellationToken cancellationToken = default)
    {
        var mode = OcrMode.Normalize(config.OcrMode);
        var language = OcrLanguages.Normalize(config.OcrLanguage);
        var tempImagePath = Path.Combine(Path.GetTempPath(), $"lolchat-ocr-warmup-{Guid.NewGuid():N}.png");
        try
        {
            using (var image = new Bitmap(8, 8))
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.Clear(Color.White);
                image.Save(tempImagePath, ImageFormat.Png);
            }

            await WaitForWorkerLockAsync("warmup", cancellationToken);
            try
            {
                ApplyPendingRestartIfNeeded();
                await EnsureWorkerAsync(mode, language, config, cancellationToken);
                var coldStartMs = _pendingColdStartMs;
                _pendingColdStartMs = null;
                var payload = await SendWorkerRequestAsync("warmup", tempImagePath, mode, language, cancellationToken);
                var warmRunMs = payload.Timing?.OcrTotalMs ?? payload.Timing?.TotalMs;
                return new OcrWarmUpResult(
                    Name,
                    payload.Mode ?? mode,
                    payload.Backend ?? _backend,
                    payload.Parameters ?? _parameters,
                    coldStartMs,
                    warmRunMs,
                    payload.Error);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RestartWorker("warmup_cancelled");
                throw;
            }
            finally
            {
                ApplyPendingRestartIfNeeded();
                _workerLock.Release();
            }
        }
        finally
        {
            DeleteTempFile(tempImagePath);
        }
    }

    public void Dispose()
    {
        ResetWorker();
        _workerLock.Dispose();
    }

    public void ResetWorker()
    {
        var lockTaken = false;
        try
        {
            lockTaken = _workerLock.Wait(0);
            if (lockTaken)
            {
                RestartWorker("reset_requested");
            }
            else
            {
                lock (_workerRestartSync)
                {
                    _pendingResetReason = "reset_deferred_busy";
                }

                WriteWorkerDebug($"{Name} reset_deferred reason=worker_lock_busy");
            }
        }
        catch (ObjectDisposedException)
        {
            // The application is already shutting down.
        }
        finally
        {
            if (lockTaken)
            {
                _workerLock.Release();
            }
        }
    }

    public bool IsWorkerReady(string mode, string language)
    {
        try
        {
            return _workerProcess is { HasExited: false }
                && _workerInput is not null
                && _workerOutput is not null
                && string.Equals(_workerMode, OcrMode.Normalize(mode), StringComparison.OrdinalIgnoreCase)
                && string.Equals(_workerLanguage, OcrLanguages.Normalize(language), StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task WaitForWorkerLockAsync(string action, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(WorkerResponseTimeout);
        try
        {
            await _workerLock.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"{Name} OCR worker 正忙，等待 {action} 超时（{WorkerResponseTimeout.TotalSeconds:0}s）。");
        }
    }

    private async Task EnsureWorkerAsync(string mode, string language, OcrConfig config, CancellationToken cancellationToken)
    {
        if (_workerProcess is { HasExited: false }
            && _workerInput is not null
            && _workerOutput is not null
            && string.Equals(_workerMode, mode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_workerLanguage, language, StringComparison.OrdinalIgnoreCase))
        {
            _lastWorkerColdStart = false;
            _lastModelAlreadyLoaded = true;
            _lastWorkerStartMs = null;
            return;
        }

        RestartWorker("ensure_worker_new_process");
        WriteWorkerDebug($"{Name} ensure_worker start mode={mode} lang={language}");

        var scriptPath = ResolveScriptPath(_scriptName);
        if (scriptPath is null)
        {
            throw new FileNotFoundException($"{Name} 本地脚本未找到：请确认 OCR\\{_scriptName} 已复制到程序目录。");
        }
        var scriptSnapshot = BuildWorkerScriptSnapshot(scriptPath);
        WriteWorkerDebug($"{Name} script={scriptPath}");
        WriteWorkerScriptSnapshot(scriptSnapshot);

        var python = await PythonEnvironmentService.FindPythonAsync(cancellationToken: cancellationToken, ocrConfig: config);
        if (python is null)
        {
            throw new InvalidOperationException(PythonEnvironmentService.GetPythonMissingMessage(config));
        }
        WriteWorkerDebug($"{Name} python={python.FileName} args={string.Join(" ", python.PrefixArguments)}");

        var startInfo = new ProcessStartInfo
        {
            FileName = python.FileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        python.AddArguments(startInfo, [scriptPath, "--worker", "--mode", mode, "--lang", language, "--output-json"]);

        var coldStart = Stopwatch.StartNew();
        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"无法启动 {Name} 本地 OCR worker。");
            WriteWorkerDebug($"{Name} process_started pid={process.Id}");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"项目 PP-OCRv5 OCR 虚拟环境不可用，无法启动 {Name}。请在设置中点击“检测/安装 PP-OCRv5 OCR 环境”。{ex.Message}", ex);
        }

        _workerProcess = process;
        _workerInput = process.StandardInput;
        _workerOutput = process.StandardOutput;
        _workerMode = mode;
        _workerLanguage = language;
        _workerStdoutNonJson.Clear();
        _ = DrainWorkerErrorsAsync(process);

        WriteWorkerDebug($"{Name} waiting_ready pid={process.Id}");
        var readyPayload = await ReadWorkerPayloadAsync(expectedRequestId: null, cancellationToken);
        WriteWorkerDebug($"{Name} ready_received pid={process.Id} error={CleanWorkerLog(readyPayload.Error)}");
        coldStart.Stop();
        if (!readyPayload.Ready)
        {
            var diagnostics = BuildWorkerDiagnostics(
                expectedRequestId: null,
                failureAction: "worker_ready",
                action: "worker_ready",
                mode,
                language,
                task: "full",
                imagePath: null);
            RestartWorker("worker_ready_failed");
            throw new OcrWorkerProcessException(
                $"{Name} worker 未返回 ready 状态。{BuildWorkerFailureDetails(null, "worker_ready", null, mode, language, "full")}",
                diagnostics);
        }

        if (!string.IsNullOrWhiteSpace(readyPayload.Error))
        {
            var diagnostics = BuildDiagnostics(readyPayload, mode, consumeColdStart: false) with
            {
                PayloadErrorKind = NormalizePayloadErrorKind(readyPayload.ErrorKind),
                RestartWorker = true
            };
            RestartWorker("worker_ready_payload_error");
            throw new OcrWorkerProcessException(readyPayload.Error, diagnostics);
        }

        _backend = readyPayload.Backend ?? "unknown";
        _parameters = readyPayload.Parameters ?? "";
        _modelInitMs = readyPayload.Timing?.ModelInitMs;
        _pendingColdStartMs = coldStart.ElapsedMilliseconds;
        _lastWorkerStartMs = coldStart.ElapsedMilliseconds;
        _lastWorkerColdStart = true;
        _lastModelAlreadyLoaded = false;
    }

    private async Task<LocalOcrPayload> SendWorkerRequestAsync(string action, string imagePath, string mode, string language, CancellationToken cancellationToken)
    {
        if (_workerProcess is not { HasExited: false } || _workerInput is null || _workerOutput is null)
        {
            throw CreateWorkerException("worker_not_started", null, action, imagePath, mode, language, "full");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var request = new LocalOcrRequest(requestId, action, imagePath, mode, language, "full");
        _lastRequest = request;
        WriteWorkerDebug($"{Name} send_request id={requestId} action={action} mode={mode} lang={language} image={imagePath}");
        var requestStopwatch = Stopwatch.StartNew();
        await _workerInput.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions).AsMemory(), cancellationToken);
        await _workerInput.FlushAsync(cancellationToken);
        WriteWorkerDebug($"{Name} request_flushed id={requestId}");
        var payload = await ReadWorkerPayloadAsync(requestId, cancellationToken);
        requestStopwatch.Stop();
        _lastRequestMs = requestStopwatch.ElapsedMilliseconds;
        return payload;
    }

    private async Task<LocalOcrPayload> ReadWorkerPayloadAsync(string? expectedRequestId, CancellationToken cancellationToken)
    {
        if (_workerOutput is null)
        {
            throw new InvalidOperationException($"{Name} OCR worker 输出流不可用。");
        }

        WriteWorkerDebug($"{Name} read_begin expected={expectedRequestId ?? "<ready>"}");
        while (true)
        {
            string? line;
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(WorkerResponseTimeout);
            try
            {
                line = await _workerOutput.ReadLineAsync(readTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && readTimeout.IsCancellationRequested)
            {
                WriteWorkerDebug($"{Name} read_timeout expected={expectedRequestId ?? "<ready>"}");
                var exception = CreateWorkerException("worker_timeout", expectedRequestId, _lastRequest?.Action, _lastRequest?.ImagePath, _lastRequest?.Mode, _lastRequest?.Lang, _lastRequest?.Task);
                RestartWorker("worker_timeout");
                throw exception;
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (line is null)
            {
                var details = BuildWorkerFailureDetails(
                    expectedRequestId,
                    _lastRequest?.Action,
                    _lastRequest?.ImagePath,
                    _lastRequest?.Mode,
                    _lastRequest?.Lang,
                    _lastRequest?.Task);
                WriteWorkerDebug($"{Name} read_eof expected={expectedRequestId ?? "<ready>"} {CleanWorkerLog(details)}");
                throw CreateWorkerException("worker_exited", expectedRequestId, _lastRequest?.Action, _lastRequest?.ImagePath, _lastRequest?.Mode, _lastRequest?.Lang, _lastRequest?.Task);
            }

            WriteWorkerDebug($"{Name} read_line expected={expectedRequestId ?? "<ready>"} text={CleanWorkerLog(line)}");
            if (!line.TrimStart().StartsWith('{'))
            {
                RememberStdoutNonJson(line);
                continue;
            }

            LocalOcrPayload? payload;
            var parseStopwatch = Stopwatch.StartNew();
            try
            {
                payload = JsonSerializer.Deserialize<LocalOcrPayload>(line, JsonOptions);
            }
            catch (JsonException)
            {
                RememberStdoutNonJson(line);
                continue;
            }
            finally
            {
                parseStopwatch.Stop();
                _lastJsonParseMs = parseStopwatch.ElapsedMilliseconds;
            }

            if (payload is null)
            {
                continue;
            }

            if (expectedRequestId is null || string.Equals(payload.RequestId, expectedRequestId, StringComparison.OrdinalIgnoreCase))
            {
                WriteWorkerDebug($"{Name} read_match expected={expectedRequestId ?? "<ready>"} request_id={payload.RequestId ?? "<none>"}");
                return payload;
            }

            if (!string.IsNullOrWhiteSpace(payload.Error) && string.IsNullOrWhiteSpace(payload.RequestId))
            {
                var exception = CreateWorkerException("unmatched_worker_error", expectedRequestId, payload.Action, _lastRequest?.ImagePath, payload.Mode, payload.Lang, payload.Task);
                RestartWorker("unmatched_worker_error");
                throw exception;
            }
        }
    }

    private async Task DrainWorkerErrorsAsync(Process process)
    {
        try
        {
            while (true)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                WriteWorkerDebug($"{Name} stderr pid={process.Id} text={CleanWorkerLog(line)}");
                _workerErrors.Enqueue(line.Trim());
                while (_workerErrors.Count > 20)
                {
                    _workerErrors.TryDequeue(out _);
                }
            }
        }
        catch
        {
            // Diagnostics only.
        }
    }

    private OcrRunDiagnostics BuildDiagnostics(LocalOcrPayload payload, string mode, bool consumeColdStart)
    {
        var coldStartMs = consumeColdStart ? _pendingColdStartMs : null;
        if (consumeColdStart)
        {
            _pendingColdStartMs = null;
        }

        var scriptSnapshot = BuildWorkerScriptSnapshot(payload.WorkerScriptPath);
        return new OcrRunDiagnostics(
            OcrDetectMs: payload.Timing?.OcrDetectMs,
            OcrRecognizeMs: payload.Timing?.OcrRecognizeMs,
            OcrFullMs: string.Equals(payload.Task, "full", StringComparison.OrdinalIgnoreCase)
                ? payload.Timing?.OcrTotalMs ?? payload.Timing?.TotalMs ?? payload.ElapsedMs
                : null,
            OcrRecognizeLinesMs: string.Equals(payload.Task, "recognize-lines", StringComparison.OrdinalIgnoreCase)
                ? payload.Timing?.OcrTotalMs ?? payload.Timing?.TotalMs ?? payload.ElapsedMs
                : null,
            OcrTotalMs: payload.Timing?.OcrTotalMs ?? payload.Timing?.TotalMs ?? payload.ElapsedMs,
            OcrRequestMs: _lastRequestMs,
            OcrInferenceMs: payload.Timing?.OcrMs ?? payload.Timing?.OcrTotalMs ?? payload.Timing?.TotalMs ?? payload.ElapsedMs,
            JsonParseMs: _lastJsonParseMs,
            ColdStartMs: coldStartMs,
            WorkerStartMs: _lastWorkerStartMs,
            WarmRunMs: coldStartMs is null ? payload.Timing?.OcrTotalMs ?? payload.Timing?.TotalMs ?? payload.ElapsedMs : null,
            ModelInitMs: payload.Timing?.ModelInitMs ?? _modelInitMs,
            Backend: payload.Backend ?? _backend,
            Mode: payload.Mode ?? mode,
            Task: payload.Task ?? "full",
            PerformanceMode: payload.PerformanceMode ?? payload.Mode ?? mode,
            Parameters: payload.Parameters ?? _parameters,
            SelectedLanguage: payload.Lang,
            PaddleOcrVersion: payload.PaddleOcrVersion,
            PaddlePaddleVersion: payload.PaddlePaddleVersion,
            DetModelName: payload.DetModelName,
            RecModelName: payload.RecModelName,
            UseSpaceChar: payload.UseSpaceChar,
            FallbackReason: payload.FallbackReason,
            UnsupportedParameters: payload.UnsupportedParameters is { Count: > 0 }
                ? string.Join(",", payload.UnsupportedParameters)
                : null,
            WorkerColdStart: _lastWorkerColdStart || coldStartMs is not null,
            ModelAlreadyLoaded: _lastModelAlreadyLoaded,
            WorkerExitCode: GetWorkerExitCode(),
            WorkerStderrTail: BuildTail(_workerErrors),
            WorkerStdoutTail: BuildTail(_workerStdoutNonJson),
            WorkerLogPath: ResolveWorkerLogPath(),
            WorkerScriptPath: payload.WorkerScriptPath ?? scriptSnapshot.WorkerScriptPath,
            WorkerScriptLastWriteTime: payload.WorkerScriptLastWriteTime ?? scriptSnapshot.WorkerScriptLastWriteTime,
            WorkerScriptSha256: payload.WorkerScriptSha256 ?? scriptSnapshot.WorkerScriptSha256,
            SourceScriptPath: scriptSnapshot.SourceScriptPath,
            SourceScriptSha256: scriptSnapshot.SourceScriptSha256,
            LastRequestId: payload.RequestId ?? _lastRequest?.RequestId,
            LastRequestAction: payload.Action ?? _lastRequest?.Action,
            LastRequestImagePath: _lastRequest?.ImagePath,
            LastRequestMode: payload.Mode ?? _lastRequest?.Mode,
            LastRequestLang: payload.Lang ?? _lastRequest?.Lang,
            LastRequestTask: payload.Task ?? _lastRequest?.Task,
            PayloadErrorKind: NormalizePayloadErrorKind(payload.ErrorKind),
            UsedFullOcr: string.Equals(payload.Task ?? "full", "full", StringComparison.OrdinalIgnoreCase),
            UsedRecognitionOnly: string.Equals(payload.Task, "recognize-lines", StringComparison.OrdinalIgnoreCase),
            RawText: payload.RawText,
            ReadingOrder: payload.ReadingOrder);
    }

    private OcrWorkerProcessException CreateWorkerException(
        string failureAction,
        string? expectedRequestId,
        string? action,
        string? imagePath,
        string? mode,
        string? language,
        string? task)
    {
        var details = BuildWorkerFailureDetails(expectedRequestId, action, imagePath, mode, language, task);
        var diagnostics = BuildWorkerDiagnostics(expectedRequestId, failureAction, action, mode, language, task, imagePath);
        return new OcrWorkerProcessException($"{Name} OCR worker exited. {details}", diagnostics);
    }

    private OcrRunDiagnostics BuildWorkerDiagnostics(
        string? expectedRequestId,
        string? failureAction,
        string? action,
        string? mode,
        string? language,
        string? task,
        string? imagePath)
    {
        var scriptSnapshot = BuildWorkerScriptSnapshot();
        var classifiedFailureAction = ClassifyWorkerFailureAction(failureAction);
        return new OcrRunDiagnostics(
            WorkerStartMs: _lastWorkerStartMs,
            OcrRequestMs: _lastRequestMs,
            JsonParseMs: _lastJsonParseMs,
            WorkerColdStart: _lastWorkerColdStart,
            ModelAlreadyLoaded: _lastModelAlreadyLoaded,
            Backend: _backend,
            Mode: mode ?? _lastRequest?.Mode ?? _workerMode ?? "stable",
            Task: task ?? _lastRequest?.Task ?? "full",
            PerformanceMode: mode ?? _lastRequest?.Mode ?? _workerMode ?? "stable",
            Parameters: _parameters,
            SelectedLanguage: language ?? _lastRequest?.Lang ?? _workerLanguage,
            WorkerExitCode: GetWorkerExitCode(),
            WorkerStderrTail: BuildTail(_workerErrors),
            WorkerStdoutTail: BuildTail(_workerStdoutNonJson),
            WorkerLogPath: ResolveWorkerLogPath(),
            WorkerScriptPath: scriptSnapshot.WorkerScriptPath,
            WorkerScriptLastWriteTime: scriptSnapshot.WorkerScriptLastWriteTime,
            WorkerScriptSha256: scriptSnapshot.WorkerScriptSha256,
            SourceScriptPath: scriptSnapshot.SourceScriptPath,
            SourceScriptSha256: scriptSnapshot.SourceScriptSha256,
            LastRequestId: expectedRequestId ?? _lastRequest?.RequestId,
            LastRequestAction: action ?? _lastRequest?.Action ?? failureAction,
            LastRequestImagePath: imagePath ?? _lastRequest?.ImagePath,
            LastRequestMode: mode ?? _lastRequest?.Mode,
            LastRequestLang: language ?? _lastRequest?.Lang,
            LastRequestTask: task ?? _lastRequest?.Task,
            PayloadErrorKind: classifiedFailureAction,
            RestartWorker: failureAction is "worker_exited" or "worker_timeout" or "worker_not_started" or "unmatched_worker_error");
    }

    private string BuildWorkerFailureDetails(
        string? expectedRequestId,
        string? action,
        string? imagePath,
        string? mode,
        string? language,
        string? task)
    {
        var scriptSnapshot = BuildWorkerScriptSnapshot();
        return string.Join(
            " ",
            [
                $"has_exited={GetWorkerHasExited().ToString().ToLowerInvariant()}",
                $"exit_code={GetWorkerExitCode()?.ToString() ?? "<none>"}",
                $"request_id={expectedRequestId ?? _lastRequest?.RequestId ?? "<none>"}",
                $"action={action ?? _lastRequest?.Action ?? "<none>"}",
                $"image_path=\"{CleanWorkerLog(imagePath ?? _lastRequest?.ImagePath)}\"",
                $"mode={mode ?? _lastRequest?.Mode ?? _workerMode ?? "<none>"}",
                $"lang={language ?? _lastRequest?.Lang ?? _workerLanguage ?? "<none>"}",
                $"task={task ?? _lastRequest?.Task ?? "<none>"}",
                $"stderr_tail=\"{CleanWorkerLog(BuildTail(_workerErrors))}\"",
                $"stdout_tail=\"{CleanWorkerLog(BuildTail(_workerStdoutNonJson))}\"",
                $"log=\"{CleanWorkerLog(ResolveWorkerLogPath())}\"",
                $"worker_script=\"{CleanWorkerLog(scriptSnapshot.WorkerScriptPath)}\"",
                $"worker_script_sha256={scriptSnapshot.WorkerScriptSha256 ?? "<none>"}",
                $"source_script_sha256={scriptSnapshot.SourceScriptSha256 ?? "<none>"}"
            ]);
    }

    private bool GetWorkerHasExited()
    {
        try
        {
            return _workerProcess is null || _workerProcess.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private int? GetWorkerExitCode()
    {
        try
        {
            return _workerProcess is { HasExited: true } ? _workerProcess.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildTail(ConcurrentQueue<string> queue)
    {
        return string.Join(" | ", queue.TakeLast(20));
    }

    private static string ResolveWorkerLogPath()
    {
        try
        {
            return Path.Combine(AppLogService.ResolveLogDirectory(), "ocr-worker-debug.log");
        }
        catch
        {
            return "ocr-worker-debug.log";
        }
    }

    private void RememberStdoutNonJson(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _workerStdoutNonJson.Enqueue(line.Trim());
        while (_workerStdoutNonJson.Count > 20)
        {
            _workerStdoutNonJson.TryDequeue(out _);
        }
    }

    private static string NormalizePayloadErrorKind(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    private static bool ShouldRestartWorkerForPayloadError(string errorKind)
    {
        return errorKind is "fatal" or "worker_lost" or "init_failed" or "worker_script_error";
    }

    private string ClassifyWorkerFailureAction(string? failureAction)
    {
        var action = string.IsNullOrWhiteSpace(failureAction) ? "worker_error" : failureAction;
        var combined = $"{BuildTail(_workerErrors)} {BuildTail(_workerStdoutNonJson)}".ToLowerInvariant();
        if (combined.Contains("no module named 'paddle", StringComparison.Ordinal)
            || combined.Contains("no module named \"paddle", StringComparison.Ordinal)
            || combined.Contains("缺少 paddle", StringComparison.Ordinal))
        {
            return "dependency_missing";
        }

        if (combined.Contains("traceback (most recent call last)", StringComparison.Ordinal)
            || combined.Contains("unboundlocalerror", StringComparison.Ordinal)
            || combined.Contains("syntaxerror", StringComparison.Ordinal)
            || (GetWorkerExitCode() == 1 && action == "worker_exited"))
        {
            return "worker_script_error";
        }

        return action;
    }

    private static List<OcrTextLine> ParseLines(LocalOcrPayload payload)
    {
        var lines = payload.Lines?
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .Select(line => new OcrTextLine
            {
                Text = line.Text.Trim(),
                Confidence = line.Confidence,
                BoundingBox = line.ToRect(),
                RawIndex = line.RawIndex,
                VisualOrder = line.VisualOrder
            })
            .ToList() ?? [];

        return ReadingOrderService.Sort(lines).Lines;
    }

    private void ApplyPendingRestartIfNeeded()
    {
        string? reason;
        lock (_workerRestartSync)
        {
            reason = _pendingResetReason;
            _pendingResetReason = null;
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            RestartWorker(reason);
        }
    }

    private void RestartWorker(string reason)
    {
        lock (_workerRestartSync)
        {
            try
            {
                if (_workerProcess is { HasExited: false })
                {
                    WriteWorkerDebug($"{Name} kill_worker pid={_workerProcess.Id} reason={CleanWorkerLog(reason)}");
                    _workerProcess.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort shutdown.
            }

            _workerInput?.Dispose();
            _workerOutput?.Dispose();
            _workerProcess?.Dispose();
            _workerInput = null;
            _workerOutput = null;
            _workerProcess = null;
            _workerMode = null;
            _workerLanguage = null;
            _pendingColdStartMs = null;
            _modelInitMs = null;
            _lastWorkerStartMs = null;
            _lastRequestMs = null;
            _lastJsonParseMs = null;
            _lastWorkerColdStart = false;
            _lastModelAlreadyLoaded = false;
            _lastRequest = null;
            _backend = "unknown";
            _parameters = "";
            _workerErrors.Clear();
            _workerStdoutNonJson.Clear();
        }
    }

    private static void WriteWorkerDebug(string line)
    {
        try
        {
            AppLogService.AppendVerboseText("ocr-worker-debug.log", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
            // Worker diagnostics should never interrupt OCR.
        }
    }

    private void WriteWorkerScriptSnapshot(WorkerScriptSnapshot snapshot)
    {
        try
        {
            AppLogService.AppendText(
                "ocr-worker-diagnostics.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name} worker_script_path=\"{snapshot.WorkerScriptPath ?? "<none>"}\" worker_script_last_write_time=\"{snapshot.WorkerScriptLastWriteTime ?? "<none>"}\" worker_script_sha256={snapshot.WorkerScriptSha256 ?? "<none>"} source_script_path=\"{snapshot.SourceScriptPath ?? "<none>"}\" source_script_sha256={snapshot.SourceScriptSha256 ?? "<none>"}{Environment.NewLine}");
        }
        catch
        {
            // Script diagnostics should never interrupt OCR.
        }
    }

    private static string CleanWorkerLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<none>";
        }

        var cleaned = value.ReplaceLineEndings(" ").Trim();
        return cleaned.Length <= 500 ? cleaned : $"{cleaned[..500]}...";
    }

    private static void DeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temp cleanup should not affect OCR flow.
        }
    }

    private static string? ResolveScriptPath(string scriptName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "OCR", scriptName),
            Path.Combine(AppContext.BaseDirectory, scriptName)
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private WorkerScriptSnapshot BuildWorkerScriptSnapshot(string? resolvedScriptPath = null)
    {
        var workerScriptPath = resolvedScriptPath ?? ResolveScriptPath(_scriptName);
        var sourceScriptPath = ResolveSourceScriptPath(_scriptName, workerScriptPath);
        return new WorkerScriptSnapshot(
            WorkerScriptPath: workerScriptPath,
            WorkerScriptLastWriteTime: FormatLastWriteTime(workerScriptPath),
            WorkerScriptSha256: ComputeSha256OrNull(workerScriptPath),
            SourceScriptPath: sourceScriptPath,
            SourceScriptSha256: ComputeSha256OrNull(sourceScriptPath));
    }

    private static string? ResolveSourceScriptPath(string scriptName, string? workerScriptPath)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "OCR", scriptName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OCR", scriptName),
            Path.Combine(Environment.CurrentDirectory, "OCR", scriptName),
            Path.Combine(Environment.CurrentDirectory, "LoLChatTranslator", "OCR", scriptName)
        };

        var workerFullPath = SafeFullPath(workerScriptPath);
        foreach (var candidate in candidates)
        {
            var fullPath = SafeFullPath(candidate);
            if (fullPath is null || !File.Exists(fullPath))
            {
                continue;
            }

            if (workerFullPath is null || !string.Equals(fullPath, workerFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath;
            }
        }

        return null;
    }

    private static string? SafeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static string? FormatLastWriteTime(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss zzz")
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ComputeSha256OrNull(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private sealed record WorkerScriptSnapshot(
        string? WorkerScriptPath,
        string? WorkerScriptLastWriteTime,
        string? WorkerScriptSha256,
        string? SourceScriptPath,
        string? SourceScriptSha256);

    private sealed record LocalOcrRequest(
        [property: JsonPropertyName("request_id")] string RequestId,
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("image_path")] string? ImagePath,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("lang")] string Lang,
        [property: JsonPropertyName("task")] string Task);

    private sealed class LocalOcrPayload
    {
        [JsonPropertyName("request_id")]
        public string? RequestId { get; set; }

        [JsonPropertyName("action")]
        public string? Action { get; set; }

        [JsonPropertyName("ready")]
        public bool Ready { get; set; }

        [JsonPropertyName("lines")]
        public List<LocalOcrLine>? Lines { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("error_kind")]
        public string? ErrorKind { get; set; }

        [JsonPropertyName("timing")]
        public LocalOcrTiming? Timing { get; set; }

        [JsonPropertyName("backend")]
        public string? Backend { get; set; }

        [JsonPropertyName("mode")]
        public string? Mode { get; set; }

        [JsonPropertyName("task")]
        public string? Task { get; set; }

        [JsonPropertyName("performance_mode")]
        public string? PerformanceMode { get; set; }

        [JsonPropertyName("parameters")]
        public string? Parameters { get; set; }

        [JsonPropertyName("success")]
        public bool? Success { get; set; }

        [JsonPropertyName("engine")]
        public string? Engine { get; set; }

        [JsonPropertyName("model_name")]
        public string? ModelName { get; set; }

        [JsonPropertyName("det_model_name")]
        public string? DetModelName { get; set; }

        [JsonPropertyName("rec_model_name")]
        public string? RecModelName { get; set; }

        [JsonPropertyName("lang")]
        public string? Lang { get; set; }

        [JsonPropertyName("use_space_char")]
        public bool? UseSpaceChar { get; set; }

        [JsonPropertyName("elapsed_ms")]
        public long? ElapsedMs { get; set; }

        [JsonPropertyName("raw_text")]
        public string? RawText { get; set; }

        [JsonPropertyName("reading_order")]
        public string? ReadingOrder { get; set; }

        [JsonPropertyName("fallback_reason")]
        public string? FallbackReason { get; set; }

        [JsonPropertyName("unsupported_parameters")]
        public List<string>? UnsupportedParameters { get; set; }

        [JsonPropertyName("paddleocr_version")]
        public string? PaddleOcrVersion { get; set; }

        [JsonPropertyName("paddlepaddle_version")]
        public string? PaddlePaddleVersion { get; set; }

        [JsonPropertyName("worker_script_path")]
        public string? WorkerScriptPath { get; set; }

        [JsonPropertyName("worker_script_last_write_time")]
        public string? WorkerScriptLastWriteTime { get; set; }

        [JsonPropertyName("worker_script_sha256")]
        public string? WorkerScriptSha256 { get; set; }
    }

    private sealed class LocalOcrTiming
    {
        [JsonPropertyName("ocr_detect_ms")]
        public long? OcrDetectMs { get; set; }

        [JsonPropertyName("ocr_recognize_ms")]
        public long? OcrRecognizeMs { get; set; }

        [JsonPropertyName("ocr_total_ms")]
        public long? OcrTotalMs { get; set; }

        [JsonPropertyName("ocr_ms")]
        public long? OcrMs { get; set; }

        [JsonPropertyName("total_ms")]
        public long? TotalMs { get; set; }

        [JsonPropertyName("model_init_ms")]
        public long? ModelInitMs { get; set; }
    }

    private sealed class LocalOcrLine
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }

        [JsonPropertyName("width")]
        public double Width { get; set; }

        [JsonPropertyName("height")]
        public double Height { get; set; }

        [JsonPropertyName("raw_index")]
        public int RawIndex { get; set; } = -1;

        [JsonPropertyName("visual_order")]
        public int VisualOrder { get; set; } = -1;

        public System.Windows.Rect? ToRect()
        {
            return Width > 0 && Height > 0
                ? new System.Windows.Rect(X, Y, Width, Height)
                : null;
        }
    }
}
