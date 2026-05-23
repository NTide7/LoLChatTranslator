using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public sealed record OcrDependencyInstallResult(
    bool Succeeded,
    string Message,
    bool RequiresElevation = false);

public sealed record OcrDependencyInstallProgress(
    int Percent,
    string Message,
    string? Detail = null);

public sealed class OcrDependencyInstallerService
{
    private const string PythonInstallerVersion = "3.11.9";
    private const string PythonInstallerFileName = $"python-{PythonInstallerVersion}-amd64.exe";
    private const string PaddlePaddlePackage = "paddlepaddle==3.3.1";
    private const string PaddleOcrPackage = "paddleocr==3.3.3";

    private static readonly Uri PythonInstallerUri =
        new($"https://www.python.org/ftp/python/{PythonInstallerVersion}/{PythonInstallerFileName}");

    private static readonly PipSource[] DefaultAndTsinghuaSources =
    [
        new("默认 PyPI", null),
        new("清华 PyPI 镜像", "https://pypi.tuna.tsinghua.edu.cn/simple")
    ];

    private static readonly PipSource[] PaddlePaddleSources =
    [
        new("默认 PyPI", null),
        new("清华 PyPI 镜像", "https://pypi.tuna.tsinghua.edu.cn/simple"),
        new("Paddle 官方 CPU 源", "https://www.paddlepaddle.org.cn/packages/stable/cpu/")
    ];

    public async Task<OcrDependencyInstallResult> InstallAllAsync(
        OcrConfig? ocrConfig = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        IProgress<OcrDependencyInstallProgress>? detailedProgress = null)
    {
        var log = new StringBuilder();

        try
        {
            Report(progress, detailedProgress, 0, "开始检测 PP-OCRv5 OCR 环境...", $"OCR 环境目录: {PythonEnvironmentService.ResolveOcrEnvironmentDirectory(ocrConfig)}");
            var basePython = await ResolveBasePythonAsync(ocrConfig, progress, detailedProgress, log, cancellationToken);
            Report(progress, detailedProgress, 30, $"Python 已就绪：{basePython.DisplayName} ({basePython.Version})", $"python: {basePython.FileName}");
            var venvPython = await EnsureVenvAsync(basePython, ocrConfig, progress, detailedProgress, log, cancellationToken);
            Report(progress, detailedProgress, 45, $"OCR 虚拟环境已就绪：{PythonEnvironmentService.ResolveOcrVenvDirectory(ocrConfig)}", $"venv python: {venvPython.FileName}");
            var summary = await InstallOcrIntoVenvAsync(venvPython, ocrConfig, progress, detailedProgress, log, cancellationToken);
            var result = BuildInstallResult(summary);
            Report(progress, detailedProgress, 100, result.Message, "PP-OCRv5 OCR 环境安装完成。");
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or HttpRequestException)
        {
            var message = ex is OperationCanceledException ? "OCR 环境安装已取消。" : ex.Message;
            Report(progress, detailedProgress, 100, message, $"ERROR: {message}");
            var details = $"{message}{Environment.NewLine}{log}";
            return new OcrDependencyInstallResult(false, details, IsPermissionFailure(ex) || OcrEnvironmentInstallRecovery.LooksLikePermissionFailure(details));
        }
    }

    public async Task<OcrDependencyInstallResult> DeleteManagedEnvironmentAsync(
        OcrConfig? ocrConfig = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            progress?.Report("正在删除项目 PP-OCRv5 OCR 虚拟环境...");
            await Task.Run(() =>
            {
                KillManagedOcrPythonProcesses(ocrConfig);
                DeleteManagedDirectory(PythonEnvironmentService.ResolveOcrVenvDirectory(ocrConfig), ocrConfig);
            }, cancellationToken);

            const string message = "已删除项目 PP-OCRv5 OCR 虚拟环境。不会影响用户自己安装的 Python 或系统 Python。";
            progress?.Report(message);
            return new OcrDependencyInstallResult(true, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var message = $"删除 OCR 环境失败：{ex.Message}";
            progress?.Report(message);
            return new OcrDependencyInstallResult(false, message);
        }
    }

    private static async Task<PythonCommand> ResolveBasePythonAsync(
        OcrConfig? ocrConfig,
        IProgress<string>? progress,
        IProgress<OcrDependencyInstallProgress>? detailedProgress,
        StringBuilder log,
        CancellationToken cancellationToken)
    {
        Report(progress, detailedProgress, 5, $"正在检测 Python 环境，要求 {PythonEnvironmentService.SupportedVersionRange}，并排除 Microsoft Store alias...");
        var python = await PythonEnvironmentService.FindBasePythonAsync(log, cancellationToken, ocrConfig);
        if (python is not null)
        {
            Report(progress, detailedProgress, 20, $"已找到合格 Python：{python.DisplayName} ({python.Version})。", $"python: {python.FileName}");
            return python;
        }

        Report(progress, detailedProgress, 22, $"未找到合格 Python，正在安装程序专用 Python {PythonInstallerVersion} x64...");
        await InstallManagedPythonAsync(ocrConfig, progress, detailedProgress, log, cancellationToken);

        var probe = await PythonEnvironmentService.ProbePythonAsync(
            PythonEnvironmentService.ResolveManagedPythonPath(ocrConfig),
            displayName: "managed Python 3.11",
            cancellationToken: cancellationToken);
        if (probe.Command is null)
        {
            throw new InvalidOperationException($"程序专用 Python 安装后仍不可用：{probe.SkipReason}");
        }

        Report(progress, detailedProgress, 28, $"程序专用 Python 已就绪：{probe.Command.Version}。", $"python: {probe.ExecutablePath ?? PythonEnvironmentService.ResolveManagedPythonPath(ocrConfig)}");
        return probe.Command;
    }

    private static async Task<PythonCommand> EnsureVenvAsync(
        PythonCommand basePython,
        OcrConfig? ocrConfig,
        IProgress<string>? progress,
        IProgress<OcrDependencyInstallProgress>? detailedProgress,
        StringBuilder log,
        CancellationToken cancellationToken)
    {
        var venvPath = PythonEnvironmentService.ResolveOcrVenvDirectory(ocrConfig);
        var venvPythonPath = PythonEnvironmentService.ResolveOcrVenvPythonPath(ocrConfig);

        if (File.Exists(venvPythonPath))
        {
            var existingProbe = await PythonEnvironmentService.ProbePythonAsync(
                venvPythonPath,
                displayName: "project OCR venv",
                requireVenv: false,
                cancellationToken: cancellationToken);
            if (existingProbe.Command is not null)
            {
                Report(progress, detailedProgress, 40, $"项目 OCR 虚拟环境已存在：{venvPath}");
                return existingProbe.Command;
            }

            log.AppendLine($"Existing OCR venv is invalid: {existingProbe.SkipReason}");
            Report(progress, detailedProgress, 34, "现有 OCR 虚拟环境不可用，正在重建...");
            DeleteManagedDirectory(venvPath, ocrConfig);
        }

        Report(progress, detailedProgress, 35, $"正在创建项目专用 OCR 虚拟环境：{venvPath}");
        Directory.CreateDirectory(PythonEnvironmentService.ResolveOcrEnvironmentDirectory(ocrConfig));
        await RunPythonAsync(basePython, ["-m", "venv", venvPath], log, cancellationToken, detailedProgress: detailedProgress, percent: 38);

        var probe = await PythonEnvironmentService.ProbePythonAsync(
            venvPythonPath,
            displayName: "project OCR venv",
            requireVenv: false,
            cancellationToken: cancellationToken);
        if (probe.Command is null)
        {
            throw new InvalidOperationException($"OCR 虚拟环境创建失败：{probe.SkipReason}");
        }

        return probe.Command;
    }

    private static async Task<OcrInstallSummary> InstallOcrIntoVenvAsync(
        PythonCommand venvPython,
        OcrConfig? ocrConfig,
        IProgress<string>? progress,
        IProgress<OcrDependencyInstallProgress>? detailedProgress,
        StringBuilder log,
        CancellationToken cancellationToken)
    {
        Report(progress, detailedProgress, 50, "正在升级 OCR 虚拟环境中的 pip / setuptools / wheel...");
        await InstallPackagesWithSourcesAsync(
            venvPython,
            ["pip", "setuptools", "wheel"],
            DefaultAndTsinghuaSources,
            "pip / setuptools / wheel",
            progress,
            detailedProgress,
            52,
            log,
            cancellationToken,
            noCache: true,
            upgrade: true);

        await InstallPaddleStackAsync(venvPython, progress, detailedProgress, log, cancellationToken);
        var versions = await ProbePaddleVersionsAsync(venvPython, progress, detailedProgress, log, cancellationToken);
        var selfTest = await RunPpOcrWorkerSelfTestAsync(venvPython, ocrConfig, progress, detailedProgress, log, cancellationToken);
        return new OcrInstallSummary(
            PythonPath: venvPython.FileName,
            VenvPath: PythonEnvironmentService.ResolveOcrVenvDirectory(ocrConfig),
            PaddleOcrVersion: versions.PaddleOcrVersion,
            PaddlePaddleVersion: versions.PaddlePaddleVersion,
            SelectedLanguage: selfTest.SelectedLanguage,
            DetModelName: selfTest.DetModelName,
            RecModelName: selfTest.RecModelName,
            Status: selfTest.Status,
            FallbackReason: selfTest.FallbackReason,
            WorkerMessage: selfTest.WorkerMessage);
    }

    private static async Task InstallPaddleStackAsync(
        PythonCommand venvPython,
        IProgress<string>? progress,
        IProgress<OcrDependencyInstallProgress>? detailedProgress,
        StringBuilder log,
        CancellationToken cancellationToken)
    {
        try
        {
            Report(progress, detailedProgress, 60, "正在安装 PaddlePaddle CPU 版...");
            await InstallPackagesWithSourcesAsync(
                venvPython,
                [PaddlePaddlePackage],
                PaddlePaddleSources,
                "PaddlePaddle",
                progress,
                detailedProgress,
                65,
                log,
                cancellationToken);
        }
        catch (PackageInstallException ex)
        {
            throw new PaddleDependencyInstallException(BuildPaddlePaddleFailureMessage(ex.Message), ex);
        }

        try
        {
            Report(progress, detailedProgress, 75, "PaddlePaddle 已安装，正在安装已验证版本 PaddleOCR 3.3.3...");
            await InstallPackagesWithSourcesAsync(
                venvPython,
                [PaddleOcrPackage],
                DefaultAndTsinghuaSources,
                "PaddleOCR 3.3.3",
                progress,
                detailedProgress,
                80,
                log,
                cancellationToken);
        }
        catch (PackageInstallException ex)
        {
            throw new PaddleDependencyInstallException(BuildPaddleOcrFailureMessage(ex.Message), ex);
        }
    }

    private static async Task<PaddleVersionProbe> ProbePaddleVersionsAsync(
        PythonCommand venvPython,
        IProgress<string>? progress,
        IProgress<OcrDependencyInstallProgress>? detailedProgress,
        StringBuilder log,
        CancellationToken cancellationToken)
    {
        Report(progress, detailedProgress, 88, "正在验证 paddlepaddle 与 paddleocr 可导入...");
        var code =
            "import json, paddle, paddleocr; " +
            "print(json.dumps({'paddlepaddle': getattr(paddle, '__version__', ''), 'paddleocr': getattr(paddleocr, '__version__', '')}, ensure_ascii=False))";
        var result = await RunPythonAsync(venvPython, ["-c", code], log, cancellationToken, detailedProgress: detailedProgress, percent: 90);
        try
        {
            using var document = JsonDocument.Parse(ExtractLastJsonObject(result.Output));
            var root = document.RootElement;
            var paddlePaddleVersion = root.TryGetProperty("paddlepaddle", out var paddlePaddleElement)
                ? paddlePaddleElement.GetString() ?? string.Empty
                : string.Empty;
            var paddleOcrVersion = root.TryGetProperty("paddleocr", out var paddleOcrElement)
                ? paddleOcrElement.GetString() ?? string.Empty
                : string.Empty;
            Report(progress, detailedProgress, 92, $"PaddleOCR version: {paddleOcrVersion}; PaddlePaddle version: {paddlePaddleVersion}");
            return new PaddleVersionProbe(paddleOcrVersion, paddlePaddleVersion);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Paddle 版本自检输出无法解析：{result.Output}", ex);
        }
    }

    private static async Task<PpOcrWorkerSelfTest> RunPpOcrWorkerSelfTestAsync(
        PythonCommand venvPython,
        OcrConfig? ocrConfig,
        IProgress<string>? progress,
        IProgress<OcrDependencyInstallProgress>? detailedProgress,
        StringBuilder log,
        CancellationToken cancellationToken)
    {
        var mode = OcrMode.Normalize(ocrConfig?.OcrMode ?? OcrMode.Standard);
        var language = OcrLanguages.Normalize(ocrConfig?.OcrLanguage ?? OcrLanguages.Auto);
        var scriptPath = ResolvePpOcrWorkerScriptPath();
        if (!File.Exists(scriptPath))
        {
            throw new InvalidOperationException($"找不到 PP-OCRv5 worker 脚本：{scriptPath}");
        }

        var scriptHash = ComputeSha256Safe(scriptPath);
        var scriptLastWriteTime = File.GetLastWriteTime(scriptPath).ToString("yyyy-MM-dd HH:mm:ss zzz");
        log.AppendLine($"PP-OCRv5 worker self-test script path: {scriptPath}");
        log.AppendLine($"PP-OCRv5 worker self-test script last write time: {scriptLastWriteTime}");
        log.AppendLine($"PP-OCRv5 worker self-test script SHA256: {scriptHash}");

        var imagePath = Path.Combine(Path.GetTempPath(), $"lolchat-ppocrv5-selftest-{Guid.NewGuid():N}.png");
        try
        {
            CreateSelfTestImage(imagePath);
            Report(progress, detailedProgress, 95, $"正在启动 PP-OCRv5 worker 自检：engine={OcrEngines.PpOcrV5Multilingual} lang={language} mode={mode}");
            var result = await RunPythonAsync(
                venvPython,
                [scriptPath, "--image", imagePath, "--mode", mode, "--lang", language, "--output-json"],
                log,
                cancellationToken,
                throwOnNonZero: false,
                detailedProgress: detailedProgress,
                percent: 96);
            log.AppendLine($"PP-OCRv5 worker self-test exit code: {result.ExitCode}");
            if (result.ExitCode != 0)
            {
                var payloadError = TryReadWorkerSelfTestError(result.Output);
                var versionProbe = await ProbePaddleVersionsForDiagnosticsAsync(venvPython, log, cancellationToken);
                var errorKind = payloadError.ErrorKind ?? ClassifyWorkerSelfTestError(result.Output, result.Error, result.ExitCode);
                throw new InvalidOperationException(
                    $"PP-OCRv5 worker 自检失败：error_kind={errorKind} exit_code={result.ExitCode} " +
                    $"stderr_tail=\"{TruncateTail(result.Error, 1600)}\" stdout_tail=\"{TruncateTail(result.Output, 1600)}\" " +
                    $"script_path=\"{scriptPath}\" script_sha256={scriptHash} python_path=\"{venvPython.FileName}\" " +
                    $"paddleocr_version={payloadError.PaddleOcrVersion ?? versionProbe.PaddleOcrVersion} " +
                    $"paddlepaddle_version={payloadError.PaddlePaddleVersion ?? versionProbe.PaddlePaddleVersion}");
            }

            return ParseWorkerSelfTest(result.Output, language);
        }
        finally
        {
            TryDeleteFile(imagePath);
        }
    }

    private static string ResolvePpOcrWorkerScriptPath()
    {
        var outputScript = Path.Combine(AppContext.BaseDirectory, "OCR", "ppocrv5_multilingual.py");
        if (File.Exists(outputScript))
        {
            return outputScript;
        }

        var sourceScript = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "OCR", "ppocrv5_multilingual.py");
        return Path.GetFullPath(sourceScript);
    }

    private static void CreateSelfTestImage(string imagePath)
    {
        using var bitmap = new Bitmap(260, 70);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        using var font = new Font("Arial", 24, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.Black);
        graphics.DrawString("hello 123", font, brush, 12, 18);
        bitmap.Save(imagePath, ImageFormat.Png);
    }

    private static PpOcrWorkerSelfTest ParseWorkerSelfTest(string output, string requestedLanguage)
    {
        try
        {
            using var document = JsonDocument.Parse(ExtractLastJsonObject(output));
            var root = document.RootElement;
            var success = ReadBool(root, "success");
            var engine = ReadString(root, "engine");
            var error = ReadString(root, "error");
            if (!success || !string.Equals(engine, OcrEngines.PpOcrV5Multilingual, StringComparison.OrdinalIgnoreCase))
            {
                var errorKind = ReadString(root, "error_kind") ?? "worker_error";
                throw new InvalidOperationException($"PP-OCRv5 worker 自检失败：error_kind={errorKind} error={error}");
            }

            var fallbackReason = ReadString(root, "fallback_reason");
            var status = string.IsNullOrWhiteSpace(fallbackReason) ? "OK" : "fallback";
            return new PpOcrWorkerSelfTest(
                SelectedLanguage: ReadString(root, "lang") ?? requestedLanguage,
                DetModelName: ReadString(root, "det_model_name") ?? "<unknown>",
                RecModelName: ReadString(root, "rec_model_name") ?? "<unknown>",
                Status: status,
                FallbackReason: fallbackReason,
                WorkerMessage: output.Trim());
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"PP-OCRv5 worker 自检输出无法解析：{output}", ex);
        }
    }

    private static async Task<PaddleVersionProbe> ProbePaddleVersionsForDiagnosticsAsync(
        PythonCommand venvPython,
        StringBuilder log,
        CancellationToken cancellationToken)
    {
        try
        {
            var code =
                "import json\n" +
                "data={}\n" +
                "try:\n import paddle; data['paddlepaddle']=getattr(paddle, '__version__', 'unknown')\n" +
                "except Exception as exc: data['paddlepaddle']='unavailable ('+str(exc)+')'\n" +
                "try:\n import paddleocr; data['paddleocr']=getattr(paddleocr, '__version__', 'unknown')\n" +
                "except Exception as exc: data['paddleocr']='unavailable ('+str(exc)+')'\n" +
                "print(json.dumps(data, ensure_ascii=False))";
            var result = await RunPythonAsync(venvPython, ["-c", code], log, cancellationToken, throwOnNonZero: false);
            if (result.ExitCode == 0)
            {
                using var document = JsonDocument.Parse(ExtractLastJsonObject(result.Output));
                var root = document.RootElement;
                return new PaddleVersionProbe(
                    ReadString(root, "paddleocr") ?? "<none>",
                    ReadString(root, "paddlepaddle") ?? "<none>");
            }

            return new PaddleVersionProbe(
                $"unavailable (exit_code={result.ExitCode})",
                $"unavailable (exit_code={result.ExitCode})");
        }
        catch (Exception ex)
        {
            return new PaddleVersionProbe($"unavailable ({ex.Message})", $"unavailable ({ex.Message})");
        }
    }

    private static WorkerSelfTestPayloadError TryReadWorkerSelfTestError(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(ExtractLastJsonObject(output));
            var root = document.RootElement;
            return new WorkerSelfTestPayloadError(
                ReadString(root, "error_kind"),
                ReadString(root, "error"),
                ReadString(root, "paddleocr_version"),
                ReadString(root, "paddlepaddle_version"));
        }
        catch
        {
            return new WorkerSelfTestPayloadError(null, null, null, null);
        }
    }

    private static string ClassifyWorkerSelfTestError(string output, string error, int exitCode)
    {
        var text = $"{output} {error}".ToLowerInvariant();
        if (text.Contains("no module named 'paddle", StringComparison.Ordinal)
            || text.Contains("no module named \"paddle", StringComparison.Ordinal)
            || text.Contains("缺少 paddle", StringComparison.Ordinal))
        {
            return "dependency_missing";
        }

        if (text.Contains("traceback (most recent call last)", StringComparison.Ordinal)
            || text.Contains("unboundlocalerror", StringComparison.Ordinal)
            || text.Contains("syntaxerror", StringComparison.Ordinal)
            || exitCode == 1)
        {
            return "worker_script_error";
        }

        return "worker_error";
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element) && element.ValueKind != JsonValueKind.Null
            ? element.ToString()
            : null;
    }

    private static bool ReadBool(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element)
            && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            && element.GetBoolean();
    }

    private static string ExtractLastJsonObject(string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
            {
                return trimmed;
            }
        }

        return output.Trim();
    }

    private static async Task InstallPackagesWithSourcesAsync(
        PythonCommand python,
        IReadOnlyList<string> packages,
        IReadOnlyList<PipSource> sources,
        string displayName,
        IProgress<string>? progress,
        IProgress<OcrDependencyInstallProgress>? detailedProgress,
        int percent,
        StringBuilder log,
        CancellationToken cancellationToken,
        bool noCache = true,
        bool upgrade = false)
    {
        var failures = new List<PipSourceFailure>();

        foreach (var source in sources)
        {
            Report(progress, detailedProgress, percent, $"正在通过 {source.Name} 安装 {displayName}...");
            var args = BuildPipInstallArguments(packages, source, noCache, upgrade);

            try
            {
                await RunPythonAsync(python, args, log, cancellationToken, detailedProgress: detailedProgress, percent: percent);
                Report(progress, detailedProgress, Math.Min(99, percent + 5), $"{displayName} 安装成功（{source.Name}）。");
                return;
            }
            catch (InvalidOperationException ex)
            {
                var details = Truncate(ex.Message, 2400);
                failures.Add(new PipSourceFailure(source.Name, details));
                log.AppendLine($"{displayName} install failed from {source.Name}: {details}");
                Report(progress, detailedProgress, percent, $"{displayName} 通过 {source.Name} 安装失败，正在尝试下一个源...", $"FAILED: {details}");
            }
        }

        throw new PackageInstallException(displayName, packages, failures);
    }

    private static List<string> BuildPipInstallArguments(
        IReadOnlyList<string> packages,
        PipSource source,
        bool noCache,
        bool upgrade)
    {
        var args = new List<string>
        {
            "-m",
            "pip",
            "install",
            "--disable-pip-version-check",
            "--timeout",
            "60",
            "--retries",
            "3"
        };

        if (noCache)
        {
            args.Add("--no-cache-dir");
        }

        if (upgrade)
        {
            args.Add("--upgrade");
        }

        if (!string.IsNullOrWhiteSpace(source.IndexUrl))
        {
            args.Add("-i");
            args.Add(source.IndexUrl);
        }

        args.AddRange(packages);
        return args;
    }

    private static async Task InstallManagedPythonAsync(
        OcrConfig? ocrConfig,
        IProgress<string>? progress,
        IProgress<OcrDependencyInstallProgress>? detailedProgress,
        StringBuilder log,
        CancellationToken cancellationToken)
    {
        string? installerPath = null;
        try
        {
            installerPath = await ResolvePythonInstallerAsync(progress, detailedProgress, log, cancellationToken);
            var environmentDirectory = PythonEnvironmentService.ResolveOcrEnvironmentDirectory(ocrConfig);
            var managedPythonDirectory = PythonEnvironmentService.ResolveManagedPythonDirectory(ocrConfig);

            Directory.CreateDirectory(environmentDirectory);
            if (Directory.Exists(managedPythonDirectory))
            {
                DeleteManagedDirectory(managedPythonDirectory, ocrConfig);
            }

            Report(progress, detailedProgress, 24, $"正在静默安装程序专用 Python 到 {managedPythonDirectory}...");
            var startInfo = new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("/quiet");
            startInfo.ArgumentList.Add("InstallAllUsers=0");
            startInfo.ArgumentList.Add("PrependPath=0");
            startInfo.ArgumentList.Add("Include_launcher=0");
            startInfo.ArgumentList.Add("Include_test=0");
            startInfo.ArgumentList.Add("Include_doc=0");
            startInfo.ArgumentList.Add("Include_pip=1");
            startInfo.ArgumentList.Add($"TargetDir={managedPythonDirectory}");
            detailedProgress?.Report(new OcrDependencyInstallProgress(
                24,
                "正在运行 Python 安装程序...",
                $"> {QuoteForDisplay(installerPath)} {FormatArguments(startInfo.ArgumentList.Cast<string>().ToArray())}"));

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 Python 安装程序。");

            await process.WaitForExitAsync(cancellationToken);
            log.AppendLine($"Python installer exit code: {process.ExitCode}");
            detailedProgress?.Report(new OcrDependencyInstallProgress(
                27,
                $"Python 安装程序退出码：{process.ExitCode}",
                $"exit_code={process.ExitCode}"));
            if (process.ExitCode is not (0 or 3010))
            {
                throw new InvalidOperationException($"Python 安装程序退出码：{process.ExitCode}");
            }
        }
        finally
        {
            TryDeleteDownloadedInstaller(installerPath);
        }
    }

    private static async Task<string> ResolvePythonInstallerAsync(
        IProgress<string>? progress,
        IProgress<OcrDependencyInstallProgress>? detailedProgress,
        StringBuilder log,
        CancellationToken cancellationToken)
    {
        var bundledInstaller = Path.Combine(AppContext.BaseDirectory, "Installers", PythonInstallerFileName);
        if (File.Exists(bundledInstaller))
        {
            Report(progress, detailedProgress, 23, "使用安装包内置 Python 3.11 x64 安装器...", $"installer: {bundledInstaller}");
            log.AppendLine($"Using bundled Python installer: {bundledInstaller}");
            LogInstallerVerification(bundledInstaller, "bundled", log);
            return bundledInstaller;
        }

        var tempInstaller = Path.Combine(Path.GetTempPath(), $"{Path.GetFileNameWithoutExtension(PythonInstallerFileName)}-{Guid.NewGuid():N}.exe");
        Report(progress, detailedProgress, 23, $"正在下载 Python {PythonInstallerVersion} x64 安装器...", $"download: {PythonInstallerUri}");
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await client.GetAsync(PythonInstallerUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using (var network = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var file = File.Create(tempInstaller))
        {
            await network.CopyToAsync(file, cancellationToken);
        }

        log.AppendLine($"Downloaded Python installer: {tempInstaller}");
        detailedProgress?.Report(new OcrDependencyInstallProgress(24, "Python 安装器下载完成。", $"downloaded: {tempInstaller}"));
        LogInstallerVerification(tempInstaller, PythonInstallerUri.ToString(), log);
        return tempInstaller;
    }

    private static void LogInstallerVerification(string installerPath, string source, StringBuilder log)
    {
        try
        {
            var hash = ComputeSha256(installerPath);
            log.AppendLine($"Python installer source: {source}");
            log.AppendLine($"Python installer SHA256: {hash}");
            try
            {
                using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(installerPath));
                using var chain = new X509Chain();
                var chainOk = chain.Build(certificate);
                log.AppendLine($"Python installer Authenticode subject: {certificate.Subject}");
                log.AppendLine($"Python installer Authenticode issuer: {certificate.Issuer}");
                log.AppendLine($"Python installer Authenticode chain_ok: {chainOk}");
            }
            catch (Exception ex)
            {
                log.AppendLine($"Python installer Authenticode unavailable: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            log.AppendLine($"Python installer verification failed: {ex.Message}");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeSha256Safe(string path)
    {
        try
        {
            return ComputeSha256(path);
        }
        catch (Exception ex)
        {
            return $"unavailable ({ex.Message})";
        }
    }

    private static void TryDeleteDownloadedInstaller(string? installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath))
        {
            return;
        }

        try
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            var fullPath = Path.GetFullPath(installerPath);
            if (fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch
        {
            // A stale downloaded installer should not affect the install result.
        }
    }

    private static async Task<PythonRunResult> RunPythonAsync(
        PythonCommand python,
        IReadOnlyList<string> arguments,
        StringBuilder log,
        CancellationToken cancellationToken,
        bool throwOnNonZero = true,
        IProgress<OcrDependencyInstallProgress>? detailedProgress = null,
        int? percent = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = python.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        python.AddArguments(startInfo, arguments);
        detailedProgress?.Report(new OcrDependencyInstallProgress(
            percent ?? -1,
            $"正在运行命令：{python.DisplayName}",
            $"> {QuoteForDisplay(python.FileName)} {FormatArguments(python.PrefixArguments.Concat(arguments))}"));

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 Python 进程。");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"无法启动 Python：{python.DisplayName}。{ex.Message}", ex);
        }

        using (process)
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;

            AppendIfNotEmpty(log, output);
            AppendIfNotEmpty(log, error);
            ReportCommandOutput(detailedProgress, percent, "stdout", output);
            ReportCommandOutput(detailedProgress, percent, "stderr", error);
            detailedProgress?.Report(new OcrDependencyInstallProgress(
                percent ?? -1,
                $"命令结束：exit_code={process.ExitCode}",
                $"exit_code={process.ExitCode}"));

            if (throwOnNonZero && process.ExitCode != 0)
            {
                var details = string.IsNullOrWhiteSpace(error) ? output : error;
                throw new InvalidOperationException($"依赖安装命令失败：{python.DisplayName} {FormatArguments(arguments)}{Environment.NewLine}{details}");
            }

            return new PythonRunResult(output, error, process.ExitCode);
        }
    }

    private static OcrDependencyInstallResult BuildInstallResult(OcrInstallSummary summary)
    {
        var message = new StringBuilder();
        message.AppendLine("PP-OCRv5 OCR 环境检测/安装完成。");
        message.AppendLine($"Engine: {OcrEngines.PpOcrV5Multilingual}");
        message.AppendLine($"Python: {summary.PythonPath}");
        message.AppendLine($"Venv: {summary.VenvPath}");
        message.AppendLine($"PaddleOCR version: {summary.PaddleOcrVersion}");
        message.AppendLine($"PaddlePaddle version: {summary.PaddlePaddleVersion}");
        message.AppendLine($"Selected language: {summary.SelectedLanguage}");
        message.AppendLine($"Det model: {summary.DetModelName}");
        message.AppendLine($"Rec model: {summary.RecModelName}");
        message.AppendLine($"Status: {summary.Status}");
        if (!string.IsNullOrWhiteSpace(summary.FallbackReason))
        {
            message.AppendLine($"FallbackReason: {summary.FallbackReason}");
        }

        return new OcrDependencyInstallResult(true, message.ToString().Trim());
    }

    private static bool IsPermissionFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException)
            {
                return true;
            }

            if (current is Win32Exception { NativeErrorCode: 5 })
            {
                return true;
            }

            if (OcrEnvironmentInstallRecovery.LooksLikePermissionFailure(current.Message))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildPaddlePaddleFailureMessage(string details)
    {
        return
            $"paddlepaddle 无法安装，PP-OCRv5 多语言版暂不可用。{Environment.NewLine}" +
            $"已尝试命令：python -m pip install --no-cache-dir {PaddlePaddlePackage}{Environment.NewLine}" +
            $"已尝试源：{string.Join("、", PaddlePaddleSources.Select(source => source.Name))}{Environment.NewLine}" +
            $"可能原因：{Environment.NewLine}" +
            $"- Python 版本/位数不合格，PaddlePaddle 需要 64-bit Python。{Environment.NewLine}" +
            $"- pip 不可用或 pip 解析失败。{Environment.NewLine}" +
            $"- 网络 SSL / PyPI 连接失败，例如 SSLError: UNEXPECTED_EOF_WHILE_READING。{Environment.NewLine}" +
            $"详细错误：{Environment.NewLine}{details}";
    }

    private static string BuildPaddleOcrFailureMessage(string details)
    {
        return
            $"paddleocr 已验证版本无法安装，但 paddlepaddle 已安装成功。{Environment.NewLine}" +
            $"已尝试命令：python -m pip install --no-cache-dir \"{PaddleOcrPackage}\"{Environment.NewLine}" +
            $"已尝试源：默认 PyPI、清华 PyPI 镜像。{Environment.NewLine}" +
            $"可能原因：网络 SSL / PyPI 连接失败、paddleocr 版本依赖解析失败，或 pip 不可用。{Environment.NewLine}" +
            $"详细错误：{Environment.NewLine}{details}";
    }

    private static void DeleteManagedDirectory(string path, OcrConfig? ocrConfig = null)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var root = Path.GetFullPath(PythonEnvironmentService.ResolveOcrEnvironmentDirectory(ocrConfig));
        var target = Path.GetFullPath(path);
        var allowedTargets = new[]
        {
            Path.GetFullPath(PythonEnvironmentService.ResolveOcrVenvDirectory(ocrConfig)),
            Path.GetFullPath(PythonEnvironmentService.ResolveManagedPythonDirectory(ocrConfig))
        };

        if (!IsPathUnderDirectory(target, root)
            || !allowedTargets.Any(allowed => allowed.Equals(target, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"拒绝删除非程序托管目录：{target}");
        }

        Directory.Delete(target, recursive: true);
    }

    private static bool IsPathUnderDirectory(string target, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return target.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void KillManagedOcrPythonProcesses(OcrConfig? ocrConfig = null)
    {
        var venvRoot = Path.GetFullPath(PythonEnvironmentService.ResolveOcrVenvDirectory(ocrConfig));
        foreach (var process in Process.GetProcessesByName("python").Concat(Process.GetProcessesByName("pythonw")))
        {
            using (process)
            {
                try
                {
                    var processPath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(processPath))
                    {
                        continue;
                    }

                    var fullPath = Path.GetFullPath(processPath);
                    if (!IsPathUnderDirectory(fullPath, venvRoot))
                    {
                        continue;
                    }

                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
                catch
                {
                    // Best effort only; deletion will report the real error if a process keeps files locked.
                }
            }
        }
    }

    private static void TryDeleteFile(string path)
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
            // Temporary self-test images are disposable.
        }
    }

    private static void AppendIfNotEmpty(StringBuilder builder, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.AppendLine(value.Trim());
        }
    }

    private static void Report(
        IProgress<string>? progress,
        IProgress<OcrDependencyInstallProgress>? detailedProgress,
        int percent,
        string message,
        string? detail = null)
    {
        progress?.Report(message);
        detailedProgress?.Report(new OcrDependencyInstallProgress(Math.Clamp(percent, 0, 100), message, detail));
    }

    private static void ReportCommandOutput(
        IProgress<OcrDependencyInstallProgress>? detailedProgress,
        int? percent,
        string streamName,
        string value)
    {
        if (detailedProgress is null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var line in value.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            detailedProgress.Report(new OcrDependencyInstallProgress(
                percent ?? -1,
                $"命令输出：{streamName}",
                $"[{streamName}] {trimmed}"));
        }
    }

    private static string QuoteForDisplay(string value)
    {
        return value.Contains(' ') || value.Contains('\t')
            ? $"\"{value}\""
            : value;
    }

    private static string FormatArguments(IEnumerable<string> arguments)
    {
        return string.Join(" ", arguments.Select(argument =>
            argument.Contains(' ') || argument.Contains('<') || argument.Contains('>') || argument.Contains(',')
                ? $"\"{argument}\""
                : argument));
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    private static string TruncateTail(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value.ReplaceLineEndings(" ").Trim();
        }

        return "..." + value[^maxLength..].ReplaceLineEndings(" ").Trim();
    }

    private sealed record PipSource(string Name, string? IndexUrl);

    private sealed record PipSourceFailure(string SourceName, string Details);

    private sealed record PythonRunResult(string Output, string Error, int ExitCode);

    private sealed record PaddleVersionProbe(
        string PaddleOcrVersion,
        string PaddlePaddleVersion);

    private sealed record PpOcrWorkerSelfTest(
        string SelectedLanguage,
        string DetModelName,
        string RecModelName,
        string Status,
        string? FallbackReason,
        string WorkerMessage);

    private sealed record WorkerSelfTestPayloadError(
        string? ErrorKind,
        string? Error,
        string? PaddleOcrVersion,
        string? PaddlePaddleVersion);

    private sealed record OcrInstallSummary(
        string PythonPath,
        string VenvPath,
        string PaddleOcrVersion,
        string PaddlePaddleVersion,
        string SelectedLanguage,
        string DetModelName,
        string RecModelName,
        string Status,
        string? FallbackReason,
        string WorkerMessage);

    private sealed class PackageInstallException(
        string displayName,
        IReadOnlyList<string> packages,
        IReadOnlyList<PipSourceFailure> failures)
        : InvalidOperationException(BuildMessage(displayName, packages, failures))
    {
        private static string BuildMessage(
            string displayName,
            IReadOnlyList<string> packages,
            IReadOnlyList<PipSourceFailure> failures)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"{displayName} 安装失败。");
            builder.AppendLine($"包：{string.Join(", ", packages)}");
            builder.AppendLine($"已尝试源：{string.Join("、", failures.Select(failure => failure.SourceName))}");
            builder.AppendLine("各源错误：");

            foreach (var failure in failures)
            {
                builder.AppendLine($"--- {failure.SourceName} ---");
                builder.AppendLine(failure.Details);
            }

            return builder.ToString().Trim();
        }
    }

    private sealed class PaddleDependencyInstallException : InvalidOperationException
    {
        public PaddleDependencyInstallException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
