using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public static class DiagnosticSnapshotService
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    public static void WriteStartupSnapshot(AppConfig config, string sessionId)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var location = assembly.Location;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
            var buildTime = File.Exists(location)
                ? File.GetLastWriteTime(location).ToString("yyyy-MM-dd HH:mm:ss zzz")
                : "unknown";

            var builder = new StringBuilder();
            builder.AppendLine($"session_id={sessionId}");
            builder.AppendLine($"app_version={version}");
            builder.AppendLine($"build_time={buildTime}");
            builder.AppendLine($"process_arch={RuntimeInformation.ProcessArchitecture}");
            builder.AppendLine($"os={RuntimeInformation.OSDescription}");
            builder.AppendLine($"ocr_engine={config.OcrConfig.OcrEngine}");
            builder.AppendLine($"ocr_language={config.OcrConfig.OcrLanguage}");
            builder.AppendLine($"ocr_mode={config.OcrConfig.OcrMode}");
            builder.AppendLine($"ocr_timeout_ms={config.OcrConfig.OcrTimeoutMs}");
            builder.AppendLine($"effective_auto_timeout_warm_ms={Math.Max(8000, config.OcrConfig.OcrTimeoutMs)}");
            builder.AppendLine($"effective_auto_timeout_cold_ms={Math.Max(30000, Math.Max(8000, config.OcrConfig.OcrTimeoutMs))}");
            builder.AppendLine($"ocr_input_policy=full_user_selected_region");
            builder.AppendLine($"dirty_crop_enabled={config.OcrConfig.EnableAdaptiveDirtyRegionOcr.ToString().ToLowerInvariant()}");
            builder.AppendLine($"text_mask_detection={config.OcrConfig.EnableTextMaskDetection.ToString().ToLowerInvariant()}");
            builder.AppendLine($"image_scale={config.OcrConfig.ImageScale:0.##}");
            builder.AppendLine($"region={config.OcrConfig.RegionX},{config.OcrConfig.RegionY},{config.OcrConfig.RegionWidth},{config.OcrConfig.RegionHeight}");
            builder.AppendLine($"virtual_screen={GetMetric(SmXVirtualScreen)},{GetMetric(SmYVirtualScreen)},{GetMetric(SmCxVirtualScreen)},{GetMetric(SmCyVirtualScreen)}");
            builder.AppendLine($"ocr_environment_dir={PythonEnvironmentService.ResolveOcrEnvironmentDirectory(config.OcrConfig)}");
            builder.AppendLine($"python_path={PythonEnvironmentService.ResolveOcrVenvPythonPath(config.OcrConfig)}");
            AppendWorkerScriptSnapshot(builder, "ppocrv5_multilingual.py");

            AppLogService.AppendText(
                "startup-diagnostics.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [StartupSnapshot]{Environment.NewLine}{builder}{Environment.NewLine}");

            _ = Task.Run(() => WritePythonSnapshotAsync(config.OcrConfig, sessionId));
        }
        catch
        {
            // Startup diagnostics must never block app launch.
        }
    }

    private static async Task WritePythonSnapshotAsync(OcrConfig ocrConfig, string sessionId)
    {
        try
        {
            var python = await PythonEnvironmentService.FindPythonAsync(ocrConfig: ocrConfig);
            if (python is null)
            {
                AppLogService.AppendText("startup-diagnostics.log", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [PythonSnapshot] session_id={sessionId} python=<not-found>{Environment.NewLine}");
                return;
            }

            using var process = new Process();
            process.StartInfo.FileName = python.FileName;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            foreach (var argument in python.PrefixArguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add("import json, sys; data={'python': sys.executable};\ntry:\n import paddle; data['paddlepaddle']=getattr(paddle,'__version__','unknown')\nexcept Exception as exc: data['paddlepaddle']='unavailable: '+str(exc)\ntry:\n import paddleocr; data['paddleocr']=getattr(paddleocr,'__version__','unknown')\nexcept Exception as exc: data['paddleocr']='unavailable: '+str(exc)\nprint(json.dumps(data, ensure_ascii=False))");
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            AppLogService.AppendText(
                "startup-diagnostics.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [PythonSnapshot] session_id={sessionId} output=\"{Clean(output)}\" stderr=\"{Clean(error)}\" exit_code={process.ExitCode}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            AppLogService.AppendText("startup-diagnostics.log", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [PythonSnapshot] session_id={sessionId} error=\"{Clean(ex.Message)}\"{Environment.NewLine}");
        }
    }

    private static int GetMetric(int index)
    {
        try
        {
            return GetSystemMetrics(index);
        }
        catch
        {
            return 0;
        }
    }

    private static void AppendWorkerScriptSnapshot(StringBuilder builder, string scriptName)
    {
        var outputScript = Path.Combine(AppContext.BaseDirectory, "OCR", scriptName);
        var sourceScript = ResolveSourceScriptPath(scriptName, outputScript);
        builder.AppendLine($"worker_script_path={outputScript}");
        builder.AppendLine($"worker_script_last_write_time={FormatLastWriteTime(outputScript)}");
        builder.AppendLine($"worker_script_sha256={ComputeSha256OrPlaceholder(outputScript)}");
        builder.AppendLine($"source_script_path={sourceScript ?? "<none>"}");
        builder.AppendLine($"source_script_sha256={ComputeSha256OrPlaceholder(sourceScript)}");
    }

    private static string? ResolveSourceScriptPath(string scriptName, string outputScript)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "OCR", scriptName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OCR", scriptName),
            Path.Combine(Environment.CurrentDirectory, "OCR", scriptName),
            Path.Combine(Environment.CurrentDirectory, "LoLChatTranslator", "OCR", scriptName)
        };

        var outputFullPath = SafeFullPath(outputScript);
        foreach (var candidate in candidates)
        {
            var fullPath = SafeFullPath(candidate);
            if (fullPath is null || !File.Exists(fullPath))
            {
                continue;
            }

            if (!string.Equals(fullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
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

    private static string FormatLastWriteTime(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss zzz")
                : "<none>";
        }
        catch
        {
            return "<error>";
        }
    }

    private static string ComputeSha256OrPlaceholder(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return "<none>";
            }

            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            return "<error>";
        }
    }

    private static string Clean(string value)
    {
        var text = value.ReplaceLineEndings(" ").Trim();
        return text.Length <= 800 ? text : $"{text[..800]}...";
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
