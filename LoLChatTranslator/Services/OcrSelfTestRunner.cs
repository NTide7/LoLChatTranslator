using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;

namespace LoLChatTranslator.Services;

public static class OcrSelfTestRunner
{
    private const string SelfTestArg = "--ocr-self-test";
    private const string ImageArg = "--image";
    private const string OutputArg = "--output";

    public static bool ShouldRun(IReadOnlyList<string> args)
    {
        return args.Any(arg => arg.Equals(SelfTestArg, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var outputPath = GetArgValue(args, OutputArg)
            ?? Path.Combine(AppLogService.ResolveLogDirectory(), $"ocr-self-test-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var imagePath = GetArgValue(args, ImageArg);
        var report = new StringBuilder();
        var exitCode = 0;

        try
        {
            var configService = new ConfigService();
            var config = configService.Load();
            using var ocrService = new OcrService();
            var total = Stopwatch.StartNew();

            report.AppendLine($"timestamp={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"base_dir={AppContext.BaseDirectory}");
            report.AppendLine($"config_path={configService.ConfigPath}");
            report.AppendLine($"engine={OcrEngines.Normalize(config.OcrConfig.OcrEngine)}");
            report.AppendLine($"mode={OcrMode.Normalize(config.OcrConfig.OcrMode)}");
            report.AppendLine($"ocr_language={OcrLanguages.Normalize(config.OcrConfig.OcrLanguage)}");
            report.AppendLine($"ocr_environment_dir={PythonEnvironmentService.ResolveOcrEnvironmentDirectory(config.OcrConfig)}");
            report.AppendLine($"python={PythonEnvironmentService.ResolveOcrVenvPythonPath(config.OcrConfig)}");
            report.AppendLine($"image={imagePath ?? "<configured_region>"}");

            OcrRecognitionResult result;
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                result = await ocrService.RecognizeChatLinesWithDiagnosticsAsync(config);
            }
            else
            {
                using var image = new Bitmap(imagePath);
                report.AppendLine($"image_size={image.Width}x{image.Height}");
                result = await ocrService.RecognizeChatLinesWithDiagnosticsAsync(config, image);
            }

            total.Stop();
            report.AppendLine($"exit=ok");
            report.AppendLine($"elapsed_ms={total.ElapsedMilliseconds}");
            AppendResult(report, result);
            exitCode = string.IsNullOrWhiteSpace(result.ErrorMessage) ? 0 : 2;
        }
        catch (Exception ex)
        {
            exitCode = 1;
            report.AppendLine($"exit=error");
            report.AppendLine(ex.ToString());
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory);
        await File.WriteAllTextAsync(outputPath, report.ToString(), Encoding.UTF8);
        return exitCode;
    }

    private static string? GetArgValue(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static void AppendResult(StringBuilder report, OcrRecognitionResult result)
    {
        report.AppendLine($"capture_succeeded={result.CaptureSucceeded}");
        report.AppendLine($"error={result.ErrorMessage ?? "<none>"}");
        report.AppendLine($"line_count={result.Lines.Count}");
        report.AppendLine($"diagnostics={FormatDiagnostics(result.Diagnostics)}");
        report.AppendLine("lines:");
        foreach (var line in result.Lines)
        {
            report.AppendLine($"  {line}");
        }
    }

    private static string FormatDiagnostics(OcrRunDiagnostics? diagnostics)
    {
        if (diagnostics is null)
        {
            return "<none>";
        }

        return string.Join(
            " ",
            [
                $"capture_ms={FormatMs(diagnostics.CaptureMs)}",
                $"crop_ms={FormatMs(diagnostics.CropMs)}",
                $"preprocess_ms={FormatMs(diagnostics.PreprocessMs)}",
                $"ocr_detect_ms={FormatMs(diagnostics.OcrDetectMs)}",
                $"ocr_recognize_ms={FormatMs(diagnostics.OcrRecognizeMs)}",
                $"ocr_total_ms={FormatMs(diagnostics.OcrTotalMs)}",
                $"total_ms={FormatMs(diagnostics.TotalMs)}",
                $"cold_start_ms={FormatMs(diagnostics.ColdStartMs)}",
                $"warm_run_ms={FormatMs(diagnostics.WarmRunMs)}",
                $"model_init_ms={FormatMs(diagnostics.ModelInitMs)}",
                $"backend=\"{diagnostics.Backend}\"",
                $"mode={diagnostics.Mode}",
                $"selected_lang={diagnostics.SelectedLanguage ?? "<none>"}",
                $"paddleocr_version={diagnostics.PaddleOcrVersion ?? "<none>"}",
                $"paddlepaddle_version={diagnostics.PaddlePaddleVersion ?? "<none>"}",
                $"det_model=\"{diagnostics.DetModelName ?? "<none>"}\"",
                $"rec_model=\"{diagnostics.RecModelName ?? "<none>"}\"",
                $"use_space_char={diagnostics.UseSpaceChar?.ToString().ToLowerInvariant() ?? "<unknown>"}",
                $"worker_script=\"{diagnostics.WorkerScriptPath ?? "<none>"}\"",
                $"worker_script_sha256={diagnostics.WorkerScriptSha256 ?? "<none>"}",
                $"source_script_sha256={diagnostics.SourceScriptSha256 ?? "<none>"}",
                $"fallback_reason=\"{diagnostics.FallbackReason ?? "<none>"}\"",
                $"same_capture={diagnostics.SameAsLastCapture}",
                $"capture_hash={diagnostics.CaptureHash ?? "<none>"}",
                $"raw_text=\"{diagnostics.RawText ?? "<none>"}\"",
                $"params=\"{diagnostics.Parameters}\""
            ]);
    }

    private static string FormatMs(long? value)
    {
        return value.HasValue ? $"{value.Value}ms" : "<unknown>";
    }
}
