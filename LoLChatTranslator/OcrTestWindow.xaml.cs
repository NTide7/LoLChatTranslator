using System.Diagnostics;
using System.IO;
using System.Drawing.Imaging;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LoLChatTranslator.Models;
using LoLChatTranslator.Services;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingColor = System.Drawing.Color;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingPen = System.Drawing.Pen;
using DrawingRectangle = System.Drawing.Rectangle;

namespace LoLChatTranslator;

public partial class OcrTestWindow : Window
{
    private readonly string _uiLanguage;
    private readonly AppConfig _config;
    private readonly ChatCleaner _chatCleaner = new(new ChannelAliasService());
    private readonly MessageNormalizer _messageNormalizer = new();
    private byte[]? _dirtyBaselineImagePng;

    public OcrTestWindow(OcrTestReport report, string uiLanguage = "zh-Hans")
        : this(report, AppConfig.CreateDefault(), uiLanguage)
    {
    }

    public OcrTestWindow(OcrTestReport report, AppConfig config, string uiLanguage = "zh-Hans")
    {
        _uiLanguage = LocalizationService.NormalizeLanguage(uiLanguage);
        _config = config;
        InitializeComponent();
        Title = UiTextLocalizer.Localize(_uiLanguage, Title);
        UiTextLocalizer.ApplyTo(this, _uiLanguage);
        _dirtyBaselineImagePng = report.CapturePreview.CapturedImagePng;
        LoadResults(report);
    }

    private void LoadResults(OcrTestReport report)
    {
        ResultsTabControl.Items.Add(new TabItem
        {
            Header = UiTextLocalizer.Localize(_uiLanguage, "屏幕截取"),
            Content = BuildCapturePreview(report)
        });

        foreach (var result in report.EngineResults)
        {
            ResultsTabControl.Items.Add(new TabItem
            {
                Header = UiTextLocalizer.Localize(_uiLanguage, result.TestName),
                Content = BuildResultPanel(result)
            });
        }

        if (report.DirtyRegionPreview is not null)
        {
            ResultsTabControl.Items.Add(new TabItem
            {
                Header = UiTextLocalizer.Localize(_uiLanguage, "动态脏区域 OCR"),
                Content = BuildDirtyRegionPanel(report)
            });
        }

        if (ResultsTabControl.Items.Count > 0)
        {
            ResultsTabControl.SelectedIndex = 0;
        }
    }

    private FrameworkElement BuildCapturePreview(OcrTestReport report)
    {
        var preview = report.CapturePreview;
        var grid = new Grid
        {
            Margin = new Thickness(12)
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var summary = new TextBlock
        {
            Text = $"{UiTextLocalizer.Text(_uiLanguage, "引擎", "引擎", "Engine", "엔진", "エンジン", "Công cụ")}: {report.EngineName}    " +
                   $"{UiTextLocalizer.Text(_uiLanguage, "模式", "模式", "Mode", "모드", "モード", "Chế độ")}: {report.Mode}    " +
                   $"{UiTextLocalizer.Text(_uiLanguage, "截图耗时", "截圖耗時", "Capture", "캡처", "キャプチャ", "Chụp")}: {preview.CaptureMs} ms    " +
                   $"SingleOcrTotalMs: {(report.IsPreprocessComparison ? report.EngineResults.FirstOrDefault()?.Diagnostics.TotalMs?.ToString() ?? "<unknown>" : report.TotalMs.ToString())} ms    " +
                   $"CompareTotalMs: {(report.IsPreprocessComparison ? report.TotalMs.ToString() : "<not run>")} ms    " +
                   $"{UiTextLocalizer.Text(_uiLanguage, "与上次截图相同", "與上次截圖相同", "Same as last capture", "이전 캡처와 동일", "前回のスクリーンショットと同じ", "Giống ảnh chụp trước")}: {preview.SameAsLastCapture}    hash={preview.CaptureHash}",
            Margin = new Thickness(0, 0, 0, 10),
            Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(summary, 0);
        grid.Children.Add(summary);

        var capturedPanel = BuildImagePanel(
            UiTextLocalizer.Localize(_uiLanguage, "屏幕截图"),
            $"{preview.CapturedWidth} x {preview.CapturedHeight}",
            preview.CapturedImagePng);

        Grid.SetRow(capturedPanel, 1);
        grid.Children.Add(capturedPanel);

        return grid;
    }

    private FrameworkElement BuildDirtyRegionPanel(OcrTestReport report)
    {
        var preview = report.DirtyRegionPreview!;
        var grid = new Grid
        {
            Margin = new Thickness(12)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(190) });

        var maskPanel = BuildImagePanel("text mask", "mask preview", preview.TextMaskImagePng);
        Grid.SetColumn(maskPanel, 0);
        Grid.SetRow(maskPanel, 0);
        grid.Children.Add(maskPanel);

        var dirtyPanel = BuildImagePanel("dirty mask / 当前首帧", "initial full scan", preview.DirtyMaskImagePng);
        Grid.SetColumn(dirtyPanel, 2);
        Grid.SetRow(dirtyPanel, 0);
        grid.Children.Add(dirtyPanel);

        var firstResult = report.EngineResults.FirstOrDefault();
        var diagnostics = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 13,
            Padding = new Thickness(12),
            Text =
                $"EngineName: {report.EngineName}{Environment.NewLine}" +
                $"SelectedLanguage: {_config.OcrConfig.OcrLanguage}{Environment.NewLine}" +
                $"Mode: {report.Mode}{Environment.NewLine}" +
                $"CaptureMs: {report.CapturePreview.CaptureMs} ms{Environment.NewLine}" +
                $"TextMaskMs: {preview.TextMaskMs} ms{Environment.NewLine}" +
                $"DirtyDetectMs: {preview.DirtyDetectMs} ms{Environment.NewLine}" +
                $"DirtyLineCount: {preview.DirtyLineCount}{Environment.NewLine}" +
                $"FullRescanReason: {preview.FullRescanReason}{Environment.NewLine}" +
                $"UsedFullOcr: {preview.UsedFullOcr.ToString().ToLowerInvariant()}{Environment.NewLine}" +
                $"UsedRecognitionOnly: {preview.UsedRecognitionOnly.ToString().ToLowerInvariant()}{Environment.NewLine}" +
                $"LocalRegionFullOcr: {(!preview.UsedFullOcr && !preview.UsedRecognitionOnly).ToString().ToLowerInvariant()}{Environment.NewLine}" +
                $"RecognitionOnlyReason: {preview.RecognitionOnlyReason}{Environment.NewLine}" +
                $"CropRegions: {preview.DirtyRegions}{Environment.NewLine}" +
                $"Note: 当前是首帧，没有上一帧可对比，所以不会产生真实 dirty mask。点击“再次截图并比较上一帧”可以生成真实 dirty diff。{Environment.NewLine}" +
                $"ActualOcrInput: {firstResult?.InputWidth ?? 0} x {firstResult?.InputHeight ?? 0}{Environment.NewLine}" +
                $"OcrMs: {FormatMs(firstResult?.Diagnostics.OcrTotalMs)}{Environment.NewLine}" +
                $"ModelInitMs: {FormatMs(firstResult?.Diagnostics.ModelInitMs)}{Environment.NewLine}" +
                $"ColdStart: {FormatBool(firstResult?.Diagnostics.WorkerColdStart)}{Environment.NewLine}"
        };
        Grid.SetColumnSpan(diagnostics, 3);
        Grid.SetRow(diagnostics, 2);
        grid.Children.Add(diagnostics);
        return grid;
    }

    private FrameworkElement BuildResultPanel(OcrEngineComparisonResult result)
    {
        var grid = new Grid
        {
            Margin = new Thickness(12)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var imagePanel = BuildImagePanel(
            UiTextLocalizer.Text(_uiLanguage, "实际送入 OCR 的图片", "實際送入 OCR 的圖片", "Actual OCR Input", "실제 OCR 입력 이미지", "実際の OCR 入力画像", "Ảnh thực đưa vào OCR"),
            $"{result.InputWidth} x {result.InputHeight}",
            result.InputImagePng);
        Grid.SetColumn(imagePanel, 0);
        grid.Children.Add(imagePanel);

        var textBox = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 13,
            Padding = new Thickness(12),
            Text = BuildResultText(result)
        };

        Grid.SetColumn(textBox, 2);
        grid.Children.Add(textBox);
        return grid;
    }

    private static FrameworkElement BuildImagePanel(string title, string sizeText, byte[] imageBytes)
    {
        var panel = new DockPanel();

        var header = new TextBlock
        {
            Text = $"{title}  ({sizeText})",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39))
        };
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);

        var image = new Image
        {
            Source = LoadImage(imageBytes),
            Stretch = Stretch.None,
            SnapsToDevicePixels = true
        };

        var scrollViewer = new ScrollViewer
        {
            Content = image,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.White
        };

        panel.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(229, 231, 235)),
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            Child = scrollViewer
        });

        return panel;
    }

    private static BitmapImage LoadImage(byte[] imageBytes)
    {
        using var stream = new MemoryStream(imageBytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private string BuildResultText(OcrEngineComparisonResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{UiTextLocalizer.Text(_uiLanguage, "引擎", "引擎", "Engine", "엔진", "エンジン", "Công cụ")}: {result.EngineName}");
        builder.AppendLine($"{UiTextLocalizer.Text(_uiLanguage, "测试组", "測試組", "Test", "테스트", "テスト", "Bài kiểm tra")}: {UiTextLocalizer.Localize(_uiLanguage, result.TestName)}");
        builder.AppendLine($"{UiTextLocalizer.Text(_uiLanguage, "参数", "參數", "Parameters", "매개변수", "パラメータ", "Tham số")}: {result.Parameters}");
        builder.AppendLine($"{UiTextLocalizer.Text(_uiLanguage, "后端", "後端", "Backend", "백엔드", "バックエンド", "Backend")}: {result.Diagnostics.Backend}");
        builder.AppendLine($"{UiTextLocalizer.Text(_uiLanguage, "模式", "模式", "Mode", "모드", "モード", "Chế độ")}: {result.Diagnostics.Mode}");
        builder.AppendLine($"EngineName: {result.EngineName}");
        builder.AppendLine($"SelectedLanguage: {result.Diagnostics.SelectedLanguage ?? _config.OcrConfig.OcrLanguage}");
        builder.AppendLine($"PaddleOCRVersion: {result.Diagnostics.PaddleOcrVersion ?? "<none>"}");
        builder.AppendLine($"PaddlePaddleVersion: {result.Diagnostics.PaddlePaddleVersion ?? "<none>"}");
        builder.AppendLine($"DetModelName: {result.Diagnostics.DetModelName ?? "<none>"}");
        builder.AppendLine($"RecModelName: {result.Diagnostics.RecModelName ?? "<none>"}");
        builder.AppendLine($"UseSpaceChar: {FormatBool(result.Diagnostics.UseSpaceChar)}");
        builder.AppendLine($"FallbackReason: {result.Diagnostics.FallbackReason ?? "<none>"}");
        builder.AppendLine($"Task: {result.Diagnostics.Task}");
        builder.AppendLine($"PerformanceMode: {result.Diagnostics.PerformanceMode}");
        builder.AppendLine($"UnsupportedParameters: {result.Diagnostics.UnsupportedParameters ?? "<none>"}");
        builder.AppendLine($"ColdStart: {FormatBool(result.Diagnostics.WorkerColdStart)}");
        builder.AppendLine($"ModelAlreadyLoaded: {FormatBool(result.Diagnostics.ModelAlreadyLoaded)}");
        builder.AppendLine($"WorkerExitCode: {result.Diagnostics.WorkerExitCode?.ToString() ?? "<none>"}");
        builder.AppendLine($"WorkerStderrTail: {result.Diagnostics.WorkerStderrTail ?? "<none>"}");
        builder.AppendLine($"WorkerStdoutTail: {result.Diagnostics.WorkerStdoutTail ?? "<none>"}");
        builder.AppendLine($"WorkerLogPath: {result.Diagnostics.WorkerLogPath ?? "<none>"}");
        builder.AppendLine($"WorkerScriptPath: {result.Diagnostics.WorkerScriptPath ?? "<none>"}");
        builder.AppendLine($"WorkerScriptLastWriteTime: {result.Diagnostics.WorkerScriptLastWriteTime ?? "<none>"}");
        builder.AppendLine($"WorkerScriptSha256: {result.Diagnostics.WorkerScriptSha256 ?? "<none>"}");
        builder.AppendLine($"SourceScriptPath: {result.Diagnostics.SourceScriptPath ?? "<none>"}");
        builder.AppendLine($"SourceScriptSha256: {result.Diagnostics.SourceScriptSha256 ?? "<none>"}");
        builder.AppendLine($"LastRequestId: {result.Diagnostics.LastRequestId ?? "<none>"}");
        builder.AppendLine($"LastRequestAction: {result.Diagnostics.LastRequestAction ?? "<none>"}");
        builder.AppendLine($"LastRequestImagePath: {result.Diagnostics.LastRequestImagePath ?? "<none>"}");
        builder.AppendLine($"LastRequestMode: {result.Diagnostics.LastRequestMode ?? "<none>"}");
        builder.AppendLine($"LastRequestLang: {result.Diagnostics.LastRequestLang ?? "<none>"}");
        builder.AppendLine($"LastRequestTask: {result.Diagnostics.LastRequestTask ?? "<none>"}");
        builder.AppendLine($"PayloadErrorKind: {result.Diagnostics.PayloadErrorKind ?? "<none>"}");
        builder.AppendLine($"RestartWorker: {FormatBool(result.Diagnostics.RestartWorker)}");
        builder.AppendLine($"ReadingOrder: {result.Diagnostics.ReadingOrder ?? "<none>"}");
        builder.AppendLine($"ActualOcrInput: {result.InputWidth} x {result.InputHeight}");
        builder.AppendLine($"OcrInputPolicy: full_user_selected_region");
        builder.AppendLine($"DirtyCropEnabled: {_config.OcrConfig.EnableAdaptiveDirtyRegionOcr.ToString().ToLowerInvariant()}");
        builder.AppendLine($"EffectiveAutoTimeoutWarmMs: {Math.Max(8000, _config.OcrConfig.OcrTimeoutMs)}");
        builder.AppendLine($"EffectiveAutoTimeoutColdMs: {Math.Max(30000, Math.Max(8000, _config.OcrConfig.OcrTimeoutMs))}");
        builder.AppendLine($"capture_ms: {FormatMs(result.Diagnostics.CaptureMs)}");
        builder.AppendLine($"text_mask_ms: {FormatMs(result.Diagnostics.TextMaskMs)}");
        builder.AppendLine($"dirty_detect_ms: {FormatMs(result.Diagnostics.DirtyDetectMs)}");
        builder.AppendLine($"crop_ms: {FormatMs(result.Diagnostics.CropMs)}");
        builder.AppendLine($"preprocess_ms: {FormatMs(result.Diagnostics.PreprocessMs)}");
        builder.AppendLine($"ocr_detect_ms: {FormatMs(result.Diagnostics.OcrDetectMs)}");
        builder.AppendLine($"ocr_recognize_ms: {FormatMs(result.Diagnostics.OcrRecognizeMs)}");
        builder.AppendLine($"ocr_full_ms: {FormatMs(result.Diagnostics.OcrFullMs)}");
        builder.AppendLine($"ocr_recognize_lines_ms: {FormatMs(result.Diagnostics.OcrRecognizeLinesMs)}");
        builder.AppendLine($"ocr_request_ms: {FormatMs(result.Diagnostics.OcrRequestMs)}");
        builder.AppendLine($"ocr_inference_ms: {FormatMs(result.Diagnostics.OcrInferenceMs)}");
        builder.AppendLine($"json_parse_ms: {FormatMs(result.Diagnostics.JsonParseMs)}");
        builder.AppendLine($"ocr_total_ms: {FormatMs(result.Diagnostics.OcrTotalMs)}");
        builder.AppendLine($"postprocess_ms: {FormatMs(result.Diagnostics.PostProcessMs)}");
        builder.AppendLine($"total_ms: {FormatMs(result.Diagnostics.TotalMs)}");
        builder.AppendLine($"cold_start_ms: {FormatMs(result.Diagnostics.ColdStartMs)}");
        builder.AppendLine($"worker_start_ms: {FormatMs(result.Diagnostics.WorkerStartMs)}");
        builder.AppendLine($"warm_run_ms: {FormatMs(result.Diagnostics.WarmRunMs)}");
        builder.AppendLine($"model_init_ms: {FormatMs(result.Diagnostics.ModelInitMs)}");
        builder.AppendLine($"dirty_line_count: {result.Diagnostics.DirtyLineCount?.ToString() ?? "<unknown>"}");
        builder.AppendLine($"full_rescan_reason: {result.Diagnostics.FullRescanReason ?? "<none>"}");
        builder.AppendLine($"used_full_ocr: {FormatBool(result.Diagnostics.UsedFullOcr)}");
        builder.AppendLine($"used_recognition_only: {FormatBool(result.Diagnostics.UsedRecognitionOnly)}");
        builder.AppendLine($"local_region_full_ocr: {FormatBool(result.Diagnostics.LocalRegionFullOcr)}");
        builder.AppendLine($"recognition_only_reason: {result.Diagnostics.RecognitionOnlyReason ?? "<none>"}");
        builder.AppendLine($"crop_regions: {result.Diagnostics.CropRegions ?? "<none>"}");
        builder.AppendLine($"same_capture: {result.Diagnostics.SameAsLastCapture}");
        builder.AppendLine($"ocr_input_image_path: {FormatPath(result.Diagnostics.OcrInputImagePath)}");
        if (!string.IsNullOrWhiteSpace(result.Diagnostics.RawText))
        {
            builder.AppendLine($"RawText: {result.Diagnostics.RawText}");
        }

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            builder.AppendLine();
            builder.AppendLine($"{UiTextLocalizer.Text(_uiLanguage, "错误", "錯誤", "Error", "오류", "エラー", "Lỗi")}:");
            builder.AppendLine(result.ErrorMessage);
            return builder.ToString();
        }

        builder.AppendLine($"{UiTextLocalizer.Text(_uiLanguage, "行数", "行數", "Lines", "줄 수", "行数", "Số dòng")}: {result.Lines.Count}");
        builder.AppendLine();

        if (result.Lines.Count == 0)
        {
            builder.AppendLine(UiTextLocalizer.Localize(_uiLanguage, "<no text>"));
            return builder.ToString();
        }

        foreach (var line in result.Lines)
        {
            var box = line.BoundingBox is { } rect
                ? $" box=({rect.X:0},{rect.Y:0},{rect.Width:0},{rect.Height:0})"
                : string.Empty;

            builder.AppendLine($"[{line.Confidence:0.00}]{box} {line.Text}");
        }

        AppendPostProcessDiagnostics(builder, result);
        return builder.ToString();
    }

    private void AppendPostProcessDiagnostics(StringBuilder builder, OcrEngineComparisonResult result)
    {
        var rawLines = result.Lines.Select(line => line.Text).ToList();
        var merge = OcrLineContinuationMerger.Merge(rawLines, result.Lines);
        if (merge.Lines.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("[PostProcess]");
        foreach (var line in merge.Lines)
        {
            var parsed = ChatDeduper.ParseChatLine(line);
            var rawBody = string.IsNullOrWhiteSpace(parsed.RawMessageBody)
                ? parsed.Message
                : parsed.RawMessageBody;
            var parsedMessage = parsed.Message;
            var cleanedMessage = _chatCleaner.CleanMessage(line, _config);
            var autoPathMessage = cleanedMessage?.Message ?? parsedMessage;
            var builtInFixed = OcrTextFixer.ApplyBuiltInFixes(autoPathMessage);
            var messageNormalized = _messageNormalizer.Normalize(
                autoPathMessage,
                _config.TranslateConfig.ToxicDisplayMode,
                _config.TranslateConfig.TargetLanguage);
            var translationInputNormalized = TranslationInputNormalizer.NormalizeForTranslation(builtInFixed);
            var finalTranslateInput = messageNormalized.NormalizedText;
            var directTranslationHit = messageNormalized.ShouldBypassTranslator
                && !string.IsNullOrWhiteSpace(messageNormalized.DirectTranslation);
            var builtInFallbackHit = OcrTextFixer.TryTranslateBuiltInPhrase(
                finalTranslateInput,
                _config.TranslateConfig.TargetLanguage,
                out var builtInFallback);
            var finalTranslationResult = directTranslationHit
                ? messageNormalized.DirectTranslation!
                : builtInFallbackHit
                    ? builtInFallback
                    : string.Empty;
            var finalFailureReason = string.IsNullOrWhiteSpace(finalTranslationResult)
                ? "ExternalTranslatorSkippedInTestWindow"
                : string.Empty;

            builder.AppendLine($"[PostProcess][RawLine] {line}");
            builder.AppendLine($"[PostProcess][RawBody] {rawBody}");
            builder.AppendLine($"[PostProcess][ParsedMessage] {parsedMessage}");
            builder.AppendLine($"[PostProcess][AutoPathMessage] {autoPathMessage}");
            builder.AppendLine($"[PostProcess][OcrTextFixer.ApplyBuiltInFixes] {builtInFixed}");
            builder.AppendLine($"[PostProcess][MessageNormalizer.Normalize] {messageNormalized.NormalizedText}");
            builder.AppendLine($"[PostProcess][TranslationInputNormalizer.NormalizeForTranslation] {translationInputNormalized}");
            builder.AppendLine($"[PostProcess][DirectTranslationHit] {directTranslationHit}");
            builder.AppendLine($"[PostProcess][DirectTranslation] {messageNormalized.DirectTranslation ?? string.Empty}");
            builder.AppendLine($"[PostProcess][BuiltInFallbackHit] {builtInFallbackHit}");
            builder.AppendLine($"[PostProcess][BuiltInFallback] {builtInFallback}");
            builder.AppendLine($"[PostProcess][FinalTranslateInput] {finalTranslateInput}");
            if (!string.IsNullOrWhiteSpace(finalTranslationResult))
            {
                builder.AppendLine($"[PostProcess][FinalTranslationResult] {finalTranslationResult}");
            }
            else
            {
                builder.AppendLine($"[PostProcess][FinalTranslationFailureReason] {finalFailureReason}");
            }
            builder.AppendLine();

            AppLogService.AppendVerboseText(
                "ocr-test-postprocess-debug.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [OcrPostProcess][RawLine] {CleanLog(line)}{Environment.NewLine}" +
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [OcrPostProcess][RawBody] {CleanLog(rawBody)}{Environment.NewLine}" +
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [OcrPostProcess][ParsedMessage] {CleanLog(parsedMessage)}{Environment.NewLine}" +
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [OcrPostProcess][OcrTextFixer.ApplyBuiltInFixes] {CleanLog(builtInFixed)}{Environment.NewLine}" +
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [OcrPostProcess][MessageNormalizer.Normalize] {CleanLog(messageNormalized.NormalizedText)}{Environment.NewLine}" +
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [OcrPostProcess][TranslationInputNormalizer.NormalizeForTranslation] {CleanLog(translationInputNormalized)}{Environment.NewLine}" +
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [OcrPostProcess][DirectTranslationHit] {directTranslationHit.ToString().ToLowerInvariant()}{Environment.NewLine}" +
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [OcrPostProcess][BuiltInFallbackHit] {builtInFallbackHit.ToString().ToLowerInvariant()}{Environment.NewLine}" +
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [OcrPostProcess][FinalTranslateInput] {CleanLog(finalTranslateInput)}{Environment.NewLine}" +
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [OcrPostProcess][FinalTranslationResult] {CleanLog(finalTranslationResult)}{Environment.NewLine}" +
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [OcrPostProcess][FinalTranslationFailureReason] {CleanLog(finalFailureReason)}{Environment.NewLine}");
        }
    }

    private static string CleanLog(string value)
    {
        return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static string FormatMs(long? value)
    {
        return value.HasValue ? $"{value.Value} ms" : "<unknown>";
    }

    private static string FormatBool(bool? value)
    {
        return value.HasValue ? value.Value.ToString().ToLowerInvariant() : "<unknown>";
    }

    private static string FormatPath(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<none>" : value;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void RunAllPreprocessComparisonButton_Click(object sender, RoutedEventArgs e)
    {
        RunAllPreprocessComparisonButton.IsEnabled = false;
        OcrTestStatusTextBlock.Text = "正在运行全部预处理对比...";
        try
        {
            using var ocrService = new OcrService();
            var report = await ocrService.CompareEnginesAsync(_config, runAllPreprocessComparisons: true);
            ResultsTabControl.Items.Clear();
            LoadResults(report);
            OcrTestStatusTextBlock.Text = "全部预处理对比完成。";
        }
        catch (Exception ex)
        {
            OcrTestStatusTextBlock.Text = $"全部预处理对比失败：{ex.Message}";
        }
        finally
        {
            RunAllPreprocessComparisonButton.IsEnabled = true;
        }
    }

    private async void CompareDirtyRegionButton_Click(object sender, RoutedEventArgs e)
    {
        CompareDirtyRegionButton.IsEnabled = false;
        OcrTestStatusTextBlock.Text = "正在截图并比较动态脏区域...";
        try
        {
            if (_dirtyBaselineImagePng is null || _dirtyBaselineImagePng.Length == 0)
            {
                throw new InvalidOperationException("没有可用于比较的上一帧截图。请先运行一次 OCR 测试。");
            }

            var captureStopwatch = Stopwatch.StartNew();
            using var currentImage = OcrService.CaptureConfiguredRegion(_config.OcrConfig)
                ?? throw new InvalidOperationException("截图失败：请检查框选范围是否在屏幕内。");
            captureStopwatch.Stop();

            using var previousImage = DecodePng(_dirtyBaselineImagePng);

            var maskStopwatch = Stopwatch.StartNew();
            using var previousFrame = TextMaskFrame.Create(previousImage);
            using var currentFrame = TextMaskFrame.Create(currentImage);
            using var textMask = currentFrame.CreateMaskPreview();
            using var dirtyMask = currentFrame.CreateDiffPreview(previousFrame);
            maskStopwatch.Stop();

            var dirtyStopwatch = Stopwatch.StartNew();
            var diff = currentFrame.Diff(previousFrame);
            var dirtyRegions = currentFrame.BuildDirtyLineRegions(previousFrame, _config.OcrConfig, out var dirtyLineCount);
            dirtyStopwatch.Stop();

            using var dirtyBoxImage = DrawRegions(currentImage, dirtyRegions);
            var fullRescanReason = ResolveDirtyComparisonReason(previousImage, currentImage, diff, dirtyRegions);
            var tab = new TabItem
            {
                Header = UiTextLocalizer.Localize(_uiLanguage, "动态脏区域 OCR / 对比"),
                Content = BuildDirtyComparisonPanel(
                    EncodePng(dirtyBoxImage),
                    EncodePng(textMask),
                    EncodePng(dirtyMask),
                    currentImage.Width,
                    currentImage.Height,
                    captureStopwatch.ElapsedMilliseconds,
                    maskStopwatch.ElapsedMilliseconds,
                    dirtyStopwatch.ElapsedMilliseconds,
                    diff,
                    dirtyRegions,
                    dirtyLineCount,
                    fullRescanReason)
            };

            ResultsTabControl.Items.Add(tab);
            ResultsTabControl.SelectedItem = tab;
            _dirtyBaselineImagePng = EncodePng(currentImage);
            OcrTestStatusTextBlock.Text = "动态脏区域对比完成，当前截图已作为新的 baseline。";
            AppLogService.AppendVerboseText(
                "ocr-test-dirty-region-debug.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} dirty_region_compare capture_ms={captureStopwatch.ElapsedMilliseconds} text_mask_ms={maskStopwatch.ElapsedMilliseconds} dirty_detect_ms={dirtyStopwatch.ElapsedMilliseconds} changed_pixels={diff.ChangedPixels} dirty_ratio={diff.Ratio:0.0000} dirty_line_count={dirtyLineCount} full_rescan_reason={CleanLog(fullRescanReason)} regions={CleanLog(FormatRegions(dirtyRegions))}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            OcrTestStatusTextBlock.Text = $"动态脏区域对比失败：{ex.Message}";
        }
        finally
        {
            CompareDirtyRegionButton.IsEnabled = true;
        }

        await Task.CompletedTask;
    }

    private FrameworkElement BuildDirtyComparisonPanel(
        byte[] dirtyBoxImagePng,
        byte[] textMaskImagePng,
        byte[] dirtyMaskImagePng,
        int width,
        int height,
        long captureMs,
        long textMaskMs,
        long dirtyDetectMs,
        TextMaskDiff diff,
        IReadOnlyList<DrawingRectangle> dirtyRegions,
        int dirtyLineCount,
        string fullRescanReason)
    {
        var grid = new Grid
        {
            Margin = new Thickness(12)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(210) });

        var boxPanel = BuildImagePanel("当前截图 / dirty boxes", $"{width} x {height}", dirtyBoxImagePng);
        Grid.SetColumn(boxPanel, 0);
        Grid.SetRow(boxPanel, 0);
        grid.Children.Add(boxPanel);

        var maskPanel = BuildImagePanel("text mask", "current", textMaskImagePng);
        Grid.SetColumn(maskPanel, 2);
        Grid.SetRow(maskPanel, 0);
        grid.Children.Add(maskPanel);

        var dirtyPanel = BuildImagePanel("dirty mask", "current - baseline", dirtyMaskImagePng);
        Grid.SetColumn(dirtyPanel, 4);
        Grid.SetRow(dirtyPanel, 0);
        grid.Children.Add(dirtyPanel);

        var diagnostics = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 13,
            Padding = new Thickness(12),
            Text =
                $"EngineName: {_config.OcrConfig.OcrEngine}{Environment.NewLine}" +
                $"SelectedLanguage: {_config.OcrConfig.OcrLanguage}{Environment.NewLine}" +
                $"Mode: {_config.OcrConfig.OcrMode}{Environment.NewLine}" +
                $"CaptureMs: {captureMs} ms{Environment.NewLine}" +
                $"TextMaskMs: {textMaskMs} ms{Environment.NewLine}" +
                $"DirtyDetectMs: {dirtyDetectMs} ms{Environment.NewLine}" +
                $"ChangedPixels: {diff.ChangedPixels}{Environment.NewLine}" +
                $"DirtyRatio: {diff.Ratio:0.0000}{Environment.NewLine}" +
                $"DirtyLineCount: {dirtyLineCount}{Environment.NewLine}" +
                $"FullRescanReason: {fullRescanReason}{Environment.NewLine}" +
                $"UsedRecognitionOnly: false{Environment.NewLine}" +
                $"RecognitionOnlyReason: api_not_supported_local_region_full_ocr_fallback{Environment.NewLine}" +
                $"LocalRegionFullOcr: {(!string.Equals(fullRescanReason, "initial_full_scan", StringComparison.OrdinalIgnoreCase) && dirtyRegions.Count > 0).ToString().ToLowerInvariant()}{Environment.NewLine}" +
                $"UsedFullOcr: {ShouldUseFullOcrForComparison(fullRescanReason).ToString().ToLowerInvariant()}{Environment.NewLine}" +
                $"CropRegions: {FormatRegions(dirtyRegions)}{Environment.NewLine}" +
                $"Note: 这是第二帧和上一帧 baseline 的真实 text mask diff；当前自动流程仍是 local-region full OCR fallback，不是 recognition-only。{Environment.NewLine}"
        };
        Grid.SetColumnSpan(diagnostics, 5);
        Grid.SetRow(diagnostics, 2);
        grid.Children.Add(diagnostics);
        return grid;
    }

    private static string ResolveDirtyComparisonReason(
        DrawingBitmap previousImage,
        DrawingBitmap currentImage,
        TextMaskDiff diff,
        IReadOnlyList<DrawingRectangle> dirtyRegions)
    {
        if (previousImage.Width != currentImage.Width || previousImage.Height != currentImage.Height)
        {
            return "frame_size_changed";
        }

        if (diff.ChangedPixels == 0)
        {
            return "text_mask_unchanged";
        }

        if (dirtyRegions.Count == 0)
        {
            return "dirty_region_unmapped";
        }

        return "dirty_lines_detected";
    }

    private static bool ShouldUseFullOcrForComparison(string reason)
    {
        return reason is "frame_size_changed" or "dirty_region_unmapped";
    }

    private static DrawingBitmap DecodePng(byte[] imageBytes)
    {
        using var stream = new MemoryStream(imageBytes);
        using var loaded = new DrawingBitmap(stream);
        return new DrawingBitmap(loaded);
    }

    private static byte[] EncodePng(DrawingBitmap image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static DrawingBitmap DrawRegions(DrawingBitmap source, IReadOnlyList<DrawingRectangle> regions)
    {
        var result = new DrawingBitmap(source);
        using var graphics = DrawingGraphics.FromImage(result);
        using var pen = new DrawingPen(DrawingColor.FromArgb(255, 239, 68, 68), 2);
        foreach (var region in regions)
        {
            if (region.Width <= 0 || region.Height <= 0)
            {
                continue;
            }

            graphics.DrawRectangle(pen, region);
        }

        return result;
    }

    private static string FormatRegions(IReadOnlyList<DrawingRectangle> regions)
    {
        return regions.Count == 0
            ? "<none>"
            : string.Join("; ", regions.Select(region => $"{region.X},{region.Y},{region.Width},{region.Height}"));
    }
}
