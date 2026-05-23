using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public sealed record PythonCommand(
    string FileName,
    IReadOnlyList<string> PrefixArguments,
    Version Version,
    string DisplayName)
{
    public void AddArguments(ProcessStartInfo startInfo, IEnumerable<string> arguments)
    {
        foreach (var argument in PrefixArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }
}

public sealed record PythonEnvironmentProbe(
    PythonCommand? Command,
    string? SkipReason,
    string? ExecutablePath = null,
    string? Architecture = null,
    string? Machine = null,
    string? PipVersion = null);

public static class PythonEnvironmentService
{
    public static readonly Version MinimumVersion = new(3, 10);

    public static readonly Version RecommendedVersion = new(3, 11);

    public static readonly Version PreferredMinimumPipVersion = new(20, 2, 2);

    public const int MaximumSupportedMajor = 3;

    public const int MaximumSupportedMinor = 12;

    public const string SupportedVersionRange = "3.10 - 3.12 x64";

    public static readonly string ManagedRootDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LOLChatTranslator");

    public static string DefaultOcrEnvironmentDirectory
        => NormalizeOcrEnvironmentDirectory(AppContext.BaseDirectory) ?? ManagedRootDirectory;

    public static readonly string ManagedPythonDirectory = Path.Combine(ManagedRootDirectory, "Python311");

    public static readonly string ManagedPythonPath = Path.Combine(ManagedPythonDirectory, "python.exe");

    public static readonly string OcrVenvDirectory = Path.Combine(ManagedRootDirectory, "ocr_env");

    public static readonly string OcrVenvPythonPath = Path.Combine(OcrVenvDirectory, "Scripts", "python.exe");

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly Regex VersionTupleRegex = new(@"\(?\s*(?<major>\d+)\s*,\s*(?<minor>\d+)\s*,\s*(?<patch>\d+)\s*\)?", RegexOptions.Compiled);
    private static readonly Regex PipVersionRegex = new(@"pip\s+(?<version>\d+(?:\.\d+){1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PyLauncherPathRegex = new(@"[A-Za-z]:\\.*?python(?:\.exe)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<PythonCommand?> FindPythonAsync(
        StringBuilder? log = null,
        CancellationToken cancellationToken = default,
        OcrConfig? ocrConfig = null)
    {
        var venvPythonPath = ResolveOcrVenvPythonPath(ocrConfig);
        var venvProbe = await TryProbePythonAsync(
            new PythonCandidate(venvPythonPath, [], "project OCR venv"),
            requirePip: true,
            requireVenv: false,
            cancellationToken);
        if (venvProbe.Command is not null)
        {
            return venvProbe.Command;
        }

        log?.AppendLine(venvProbe.SkipReason ?? "Project OCR venv not available.");
        return null;
    }

    public static async Task<PythonCommand?> FindBasePythonAsync(
        StringBuilder? log = null,
        CancellationToken cancellationToken = default,
        OcrConfig? ocrConfig = null)
    {
        foreach (var candidate in await GetBasePythonCandidatesAsync(log, cancellationToken, ocrConfig))
        {
            var probe = await TryProbePythonAsync(candidate, requirePip: true, requireVenv: true, cancellationToken);
            if (probe.Command is not null)
            {
                log?.AppendLine($"Python accepted: {probe.Command.DisplayName} ({probe.Command.Version}, {probe.Architecture}, {probe.Machine}, pip {probe.PipVersion})");
                return probe.Command;
            }

            log?.AppendLine(probe.SkipReason ?? $"Python probe skipped: {candidate.DisplayName}");
        }

        return null;
    }

    public static async Task<PythonEnvironmentProbe> ProbePythonAsync(
        string fileName,
        IReadOnlyList<string>? prefixArguments = null,
        string? displayName = null,
        bool requirePip = true,
        bool requireVenv = true,
        CancellationToken cancellationToken = default)
    {
        return await TryProbePythonAsync(
            new PythonCandidate(fileName, prefixArguments ?? [], displayName ?? fileName),
            requirePip,
            requireVenv,
            cancellationToken);
    }

    public static string GetPythonMissingMessage(OcrConfig? ocrConfig = null)
    {
        return $"未检测到项目 PP-OCRv5 OCR 环境。请在设置中点击“检测/安装 PP-OCRv5 OCR 环境”，程序会创建 {ResolveOcrVenvDirectory(ocrConfig)} 并把 paddlepaddle 与 paddleocr 3.x 安装到这个专用环境。";
    }

    public static string ResolveOcrEnvironmentDirectory(OcrConfig? ocrConfig = null)
    {
        return NormalizeOcrEnvironmentDirectory(ocrConfig?.OcrEnvironmentDirectory)
            ?? DefaultOcrEnvironmentDirectory;
    }

    public static string? NormalizeOcrEnvironmentDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(expanded))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(expanded);
        }
        catch
        {
            return null;
        }
    }

    public static string ResolveManagedPythonDirectory(OcrConfig? ocrConfig = null)
        => Path.Combine(ResolveOcrEnvironmentDirectory(ocrConfig), "Python311");

    public static string ResolveManagedPythonPath(OcrConfig? ocrConfig = null)
        => Path.Combine(ResolveManagedPythonDirectory(ocrConfig), "python.exe");

    public static string ResolveOcrVenvDirectory(OcrConfig? ocrConfig = null)
        => Path.Combine(ResolveOcrEnvironmentDirectory(ocrConfig), "ocr_env");

    public static string ResolveOcrVenvPythonPath(OcrConfig? ocrConfig = null)
        => Path.Combine(ResolveOcrVenvDirectory(ocrConfig), "Scripts", "python.exe");

    public static bool IsSupportedForOcrDependencies(Version version)
    {
        return version >= MinimumVersion
            && version.Major == MaximumSupportedMajor
            && version.Minor <= MaximumSupportedMinor;
    }

    private static async Task<List<PythonCandidate>> GetBasePythonCandidatesAsync(
        StringBuilder? log,
        CancellationToken cancellationToken,
        OcrConfig? ocrConfig)
    {
        var candidates = new List<PythonCandidate>
        {
            new(ResolveManagedPythonPath(ocrConfig), [], "managed Python 3.11")
        };

        candidates.AddRange(await GetPyLauncherCandidatesAsync(log, cancellationToken));
        candidates.Add(new PythonCandidate("python", [], "PATH python"));
        candidates.Add(new PythonCandidate("python3", [], "PATH python3"));
        candidates.AddRange(GetCommonPathCandidates());

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return candidates
            .Where(candidate => seen.Add(candidate.Key))
            .ToList();
    }

    private static async Task<IEnumerable<PythonCandidate>> GetPyLauncherCandidatesAsync(
        StringBuilder? log,
        CancellationToken cancellationToken)
    {
        var output = await RunCommandForProbeAsync("py", ["-0p"], cancellationToken);
        if (output is null)
        {
            log?.AppendLine("py launcher skipped: py -0p failed or timed out.");
            return [];
        }

        var candidates = new List<PythonCandidate>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("3.10", StringComparison.Ordinal)
                && !line.Contains("3.11", StringComparison.Ordinal)
                && !line.Contains("3.12", StringComparison.Ordinal))
            {
                continue;
            }

            var match = PyLauncherPathRegex.Match(line);
            if (match.Success)
            {
                var path = match.Value.Trim();
                candidates.Add(new PythonCandidate(path, [], $"py launcher {line.Trim()}"));
            }
        }

        return candidates
            .OrderBy(candidate => ResolveVersionPriority(candidate.FileName))
            .ToList();
    }

    private static IEnumerable<PythonCandidate> GetCommonPathCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        yield return new PythonCandidate(
            Path.Combine(localAppData, "Programs", "Python", "Python311", "python.exe"),
            [],
            "LocalAppData Python311");
        yield return new PythonCandidate(
            Path.Combine(localAppData, "Programs", "Python", "Python312", "python.exe"),
            [],
            "LocalAppData Python312");
        yield return new PythonCandidate(
            Path.Combine(programFiles, "Python311", "python.exe"),
            [],
            "Program Files Python311");
        yield return new PythonCandidate(
            Path.Combine(programFiles, "Python312", "python.exe"),
            [],
            "Program Files Python312");
    }

    private static int ResolveVersionPriority(string value)
    {
        if (value.Contains("311", StringComparison.OrdinalIgnoreCase) || value.Contains("3.11", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (value.Contains("312", StringComparison.OrdinalIgnoreCase) || value.Contains("3.12", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (value.Contains("310", StringComparison.OrdinalIgnoreCase) || value.Contains("3.10", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
    }

    private static async Task<PythonEnvironmentProbe> TryProbePythonAsync(
        PythonCandidate candidate,
        bool requirePip,
        bool requireVenv,
        CancellationToken cancellationToken)
    {
        if (IsWindowsAppsAlias(candidate.FileName))
        {
            return PythonEnvironmentProbeSkip(candidate, "Microsoft Store WindowsApps alias is not accepted.");
        }

        var probeOutput = await RunPythonForProbeAsync(
            candidate,
            ["-c", "import sys, platform; print(sys.executable); print(sys.version_info[:3]); print(platform.architecture()[0]); print(platform.machine())"],
            cancellationToken);
        if (probeOutput is null)
        {
            return PythonEnvironmentProbeSkip(candidate, "probe command failed or timed out.");
        }

        var lines = probeOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .ToList();
        if (lines.Count < 4)
        {
            return PythonEnvironmentProbeSkip(candidate, $"probe output incomplete: {probeOutput.Trim()}");
        }

        var executablePath = lines[0];
        if (IsWindowsAppsAlias(executablePath))
        {
            return PythonEnvironmentProbeSkip(candidate, $"Microsoft Store WindowsApps alias is not accepted: {executablePath}");
        }

        var version = ParseVersionTuple(lines[1]);
        if (version is null)
        {
            return PythonEnvironmentProbeSkip(candidate, $"version not detected: {lines[1]}");
        }

        var architecture = lines[2];
        var machine = lines[3];
        if (!architecture.Contains("64", StringComparison.OrdinalIgnoreCase))
        {
            return PythonEnvironmentProbeSkip(candidate, $"Python is not 64-bit: {architecture}, {machine}");
        }

        if (!IsSupportedForOcrDependencies(version))
        {
            return PythonEnvironmentProbeSkip(candidate, $"Python {version} is not supported. Use {SupportedVersionRange}; Python 3.13+ requires manual confirmation and is not selected automatically.");
        }

        string? pipVersion = null;
        if (requirePip)
        {
            var pipOutput = await RunPythonForProbeAsync(candidate, ["-m", "pip", "--version"], cancellationToken);
            if (string.IsNullOrWhiteSpace(pipOutput))
            {
                return PythonEnvironmentProbeSkip(candidate, "pip is not available.");
            }

            pipVersion = ParsePipVersion(pipOutput)?.ToString() ?? "unknown";
        }

        if (requireVenv)
        {
            var venvOutput = await RunPythonForProbeAsync(candidate, ["-c", "import venv; print('venv ok')"], cancellationToken);
            if (venvOutput?.Contains("venv ok", StringComparison.OrdinalIgnoreCase) != true)
            {
                return PythonEnvironmentProbeSkip(candidate, "venv is not available.");
            }
        }

        return new PythonEnvironmentProbe(
            new PythonCommand(candidate.FileName, candidate.PrefixArguments, version, candidate.DisplayName),
            null,
            executablePath,
            architecture,
            machine,
            pipVersion);
    }

    private static PythonEnvironmentProbe PythonEnvironmentProbeSkip(PythonCandidate candidate, string reason)
    {
        return new PythonEnvironmentProbe(null, $"Python probe skipped: {candidate.DisplayName} ({reason})");
    }

    private static async Task<string?> RunPythonForProbeAsync(
        PythonCandidate candidate,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        return await RunCommandForProbeAsync(candidate.FileName, candidate.PrefixArguments.Concat(arguments).ToList(), cancellationToken);
    }

    private static async Task<string?> RunCommandForProbeAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProbeTimeout);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await process.WaitForExitAsync(timeoutCts.Token);
            var output = await outputTask;
            var error = await errorTask;

            return process.ExitCode == 0
                ? $"{output}{Environment.NewLine}{error}"
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsWindowsAppsAlias(string value)
    {
        return value.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
    }

    private static Version? ParseVersionTuple(string value)
    {
        var match = VersionTupleRegex.Match(value);
        if (!match.Success)
        {
            return null;
        }

        return new Version(
            int.Parse(match.Groups["major"].Value),
            int.Parse(match.Groups["minor"].Value),
            int.Parse(match.Groups["patch"].Value));
    }

    private static Version? ParsePipVersion(string value)
    {
        var match = PipVersionRegex.Match(value);
        return match.Success && Version.TryParse(match.Groups["version"].Value, out var version)
            ? version
            : null;
    }

    private sealed record PythonCandidate(
        string FileName,
        IReadOnlyList<string> PrefixArguments,
        string DisplayName)
    {
        public string Key => $"{FileName}|{string.Join(" ", PrefixArguments)}";
    }
}
