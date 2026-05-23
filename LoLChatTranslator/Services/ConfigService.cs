using System.IO;
using System.Text.Json;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public sealed class ConfigService
{
    private const string ConfigFileName = "appsettings.json";
    private const int CurrentConfigSchemaVersion = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string ConfigPath { get; } = Path.Combine(PythonEnvironmentService.ManagedRootDirectory, ConfigFileName);

    private static string BundledConfigPath => Path.Combine(AppContext.BaseDirectory, ConfigFileName);

    public AppConfig Load()
    {
        try
        {
            var configPath = File.Exists(ConfigPath)
                ? ConfigPath
                : File.Exists(BundledConfigPath)
                    ? BundledConfigPath
                    : null;

            if (configPath is null)
            {
                var defaultConfig = AppConfig.CreateDefault();
                Save(defaultConfig);
                return defaultConfig;
            }

            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            var normalizedConfig = NormalizeConfig(config ?? ResetAfterInvalidConfig());
            if (!File.Exists(ConfigPath))
            {
                Save(normalizedConfig);
            }

            return normalizedConfig;
        }
        catch
        {
            return ResetAfterInvalidConfig();
        }
    }

    public void Save(AppConfig config)
    {
        try
        {
            var directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(config, JsonOptions);
            WriteAllTextAtomic(ConfigPath, json);
        }
        catch
        {
            // Configuration errors should not crash the translator.
            // The caller keeps using the in-memory configuration.
        }
    }

    private AppConfig ResetAfterInvalidConfig()
    {
        BackupBrokenConfig();

        var defaultConfig = AppConfig.CreateDefault();
        Save(defaultConfig);
        return defaultConfig;
    }

    private static void WriteAllTextAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(
            directory ?? AppContext.BaseDirectory,
            $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, content);
        try
        {
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Stale temp files are harmless; the next save will use a new name.
            }
        }
    }

    private AppConfig NormalizeConfig(AppConfig config)
    {
        var changed = false;
        var loadedSchemaVersion = config.ConfigSchemaVersion;

        if (config.OcrConfig is null)
        {
            config.OcrConfig = new OcrConfig();
            changed = true;
        }

        if (config.TranslateConfig is null)
        {
            config.TranslateConfig = new TranslateConfig();
            changed = true;
        }

        if (config.FilterConfig is null)
        {
            config.FilterConfig = new FilterConfig();
            changed = true;
        }

        if (config.OverlayConfig is null)
        {
            config.OverlayConfig = new OverlayConfig();
            changed = true;
        }

        if (config.HotkeyConfig is null)
        {
            config.HotkeyConfig = new HotkeyConfig();
            changed = true;
        }

        if (loadedSchemaVersion < CurrentConfigSchemaVersion)
        {
            config.ConfigSchemaVersion = CurrentConfigSchemaVersion;
            changed = true;
            LogConfigMigration($"Migrated config schema {loadedSchemaVersion} -> {CurrentConfigSchemaVersion}.");
        }

        if (!config.EnableAdvancedSettings && ApplyStandardOcrPolicy(config))
        {
            changed = true;
            LogConfigMigration("Applied standard OCR policy: full user-selected region, no dirty crop/text-mask crop/fixed-bottom/image enhancement.");
        }

        var normalizedUiLanguage = LocalizationService.NormalizeLanguage(config.UiLanguage);
        if (!string.Equals(config.UiLanguage, normalizedUiLanguage, StringComparison.OrdinalIgnoreCase))
        {
            config.UiLanguage = normalizedUiLanguage;
            changed = true;
        }

        var normalizedOcrEngine = OcrEngines.Normalize(config.OcrConfig.OcrEngine, out var migratedOcrEngineFrom);
        if (!string.Equals(config.OcrConfig.OcrEngine, normalizedOcrEngine, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(migratedOcrEngineFrom))
            {
                LogConfigMigration($"Migrated OCR engine {migratedOcrEngineFrom} -> {normalizedOcrEngine}");
            }

            config.OcrConfig.OcrEngine = normalizedOcrEngine;
            changed = true;
        }

        var normalizedOcrLanguage = OcrLanguages.Normalize(config.OcrConfig.OcrLanguage);
        if (!string.Equals(config.OcrConfig.OcrLanguage, normalizedOcrLanguage, StringComparison.OrdinalIgnoreCase))
        {
            config.OcrConfig.OcrLanguage = normalizedOcrLanguage;
            changed = true;
        }

        var normalizedOcrMode = OcrMode.Normalize(config.OcrConfig.OcrMode);
        if (!string.Equals(config.OcrConfig.OcrMode, normalizedOcrMode, StringComparison.OrdinalIgnoreCase))
        {
            config.OcrConfig.OcrMode = normalizedOcrMode;
            changed = true;
        }

        var normalizedOcrEnvironmentDirectory = PythonEnvironmentService.NormalizeOcrEnvironmentDirectory(config.OcrConfig.OcrEnvironmentDirectory);
        if (string.Equals(
                normalizedOcrEnvironmentDirectory,
                PythonEnvironmentService.NormalizeOcrEnvironmentDirectory(PythonEnvironmentService.ManagedRootDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            normalizedOcrEnvironmentDirectory = null;
            LogConfigMigration("Migrated old OCR environment default from LocalAppData to the application folder.");
        }

        if (!string.Equals(config.OcrConfig.OcrEnvironmentDirectory, normalizedOcrEnvironmentDirectory ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            config.OcrConfig.OcrEnvironmentDirectory = normalizedOcrEnvironmentDirectory ?? string.Empty;
            changed = true;
        }

        if (config.OcrConfig.CaptureIntervalMs is < 250 or > 5000)
        {
            config.OcrConfig.CaptureIntervalMs = 1200;
            changed = true;
        }

        if (config.OcrConfig.ImageScale is < 1 or > 4)
        {
            config.OcrConfig.ImageScale = 1;
            changed = true;
        }

        if (config.OcrConfig.RealtimeBottomHeight is < 60 or > 260)
        {
            config.OcrConfig.RealtimeBottomHeight = 110;
            changed = true;
        }

        if (config.OcrConfig.RealtimeDetectionLineCount is < 3 or > 12)
        {
            config.OcrConfig.RealtimeDetectionLineCount = 5;
            changed = true;
        }

        if (config.OcrConfig.OcrTriggerMinIntervalMs is < 300 or > 5000)
        {
            config.OcrConfig.OcrTriggerMinIntervalMs = 300;
            changed = true;
        }

        if (config.OcrConfig.TextMaskDiffThreshold is < 0.03 or > 0.8)
        {
            config.OcrConfig.TextMaskDiffThreshold = 0.18;
            changed = true;
        }

        if (config.OcrConfig.DirtyRegionPaddingX is < 0 or > 80)
        {
            config.OcrConfig.DirtyRegionPaddingX = 12;
            changed = true;
        }

        if (config.OcrConfig.DirtyRegionPaddingY is < 0 or > 80)
        {
            config.OcrConfig.DirtyRegionPaddingY = 8;
            changed = true;
        }

        if (config.OcrConfig.FullRescanIntervalMs is < 1200 or > 60000)
        {
            config.OcrConfig.FullRescanIntervalMs = 8000;
            changed = true;
        }

        if (config.OcrConfig.MaxDirtyRegionRatioBeforeFullScan is < 0.05 or > 0.95)
        {
            config.OcrConfig.MaxDirtyRegionRatioBeforeFullScan = 0.35;
            changed = true;
        }

        if (config.OcrConfig.MinTextMaskChangedPixels is < 1 or > 10000)
        {
            config.OcrConfig.MinTextMaskChangedPixels = 30;
            changed = true;
        }

        if (config.OcrConfig.DirtyLineBatchSize is < 1 or > 16)
        {
            config.OcrConfig.DirtyLineBatchSize = 4;
            changed = true;
        }

        if (config.OcrConfig.OcrTimeoutMs is < 500 or > 15000)
        {
            config.OcrConfig.OcrTimeoutMs = 3000;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(config.TranslateConfig.TranslateEngine)
            || config.TranslateConfig.TranslateEngine.Equals("Mock", StringComparison.OrdinalIgnoreCase))
        {
            config.TranslateConfig.TranslateEngine = TranslatorEngines.MyMemoryFree;
            changed = true;
        }
        else if (IsLegacyAiEngine(config.TranslateConfig.TranslateEngine))
        {
            ApplyLegacyAiDefaults(config.TranslateConfig);
            config.TranslateConfig.TranslateEngine = TranslatorEngines.AiApi;
            changed = true;
            LogConfigMigration("Merged legacy AI translator option into AI API.");
        }

        var normalizedEngine = TranslatorEngines.Normalize(config.TranslateConfig.TranslateEngine);
        if (!string.Equals(config.TranslateConfig.TranslateEngine, normalizedEngine, StringComparison.OrdinalIgnoreCase))
        {
            config.TranslateConfig.TranslateEngine = normalizedEngine;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(config.TranslateConfig.SourceLanguage))
        {
            config.TranslateConfig.SourceLanguage = "auto";
            changed = true;
        }

        var normalizedTargetLanguage = TranslatorLanguage.NormalizeTargetLanguage(config.TranslateConfig.TargetLanguage);
        if (!string.Equals(config.TranslateConfig.TargetLanguage, normalizedTargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            config.TranslateConfig.TargetLanguage = normalizedTargetLanguage;
            changed = true;
        }

        var overlayInputTargetLanguage = config.TranslateConfig.OverlayInputTargetLanguage;
        var normalizedOverlayInputTargetLanguage = string.Equals(overlayInputTargetLanguage, ChatLanguageDetector.AutoReverse, StringComparison.OrdinalIgnoreCase)
            ? ChatLanguageDetector.AutoReverse
            : TranslatorLanguage.NormalizeTargetLanguage(config.TranslateConfig.OverlayInputTargetLanguage);
        if (!string.Equals(config.TranslateConfig.OverlayInputTargetLanguage, normalizedOverlayInputTargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            config.TranslateConfig.OverlayInputTargetLanguage = normalizedOverlayInputTargetLanguage;
            changed = true;
        }

        var normalizedOverlayInputDefaultTargetLanguage = TranslatorLanguage.NormalizeTargetLanguage(config.TranslateConfig.OverlayInputDefaultTargetLanguage);
        if (!string.Equals(config.TranslateConfig.OverlayInputDefaultTargetLanguage, normalizedOverlayInputDefaultTargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            config.TranslateConfig.OverlayInputDefaultTargetLanguage = normalizedOverlayInputDefaultTargetLanguage;
            changed = true;
        }

        if (config.TranslateConfig.TimeoutSeconds is < 5 or > 120)
        {
            config.TranslateConfig.TimeoutSeconds = TranslatorEngines.ResolveTimeoutSeconds(config.TranslateConfig);
            changed = true;
        }

        if (config.TranslateConfig.ApiBase is null)
        {
            config.TranslateConfig.ApiBase = string.Empty;
            changed = true;
        }

        if (config.TranslateConfig.ApiKey is null)
        {
            config.TranslateConfig.ApiKey = string.Empty;
            changed = true;
        }
        else if (TranslatorCredentialStore.ProtectApiKeyInConfig(config.TranslateConfig))
        {
            changed = true;
        }

        if (config.TranslateConfig.Model is null)
        {
            config.TranslateConfig.Model = string.Empty;
            changed = true;
        }

        var existingExcludedPlayers = config.TranslateConfig.ExcludedPlayers ?? [];
        var normalizedExcludedPlayers = PlayerExclusionService.NormalizeEntries(existingExcludedPlayers);
        if (normalizedExcludedPlayers.Count != existingExcludedPlayers.Count
            || normalizedExcludedPlayers.Where((entry, index) =>
                !entry.RiotId.Equals(existingExcludedPlayers[index].RiotId, StringComparison.Ordinal)
                || !entry.NormalizedName.Equals(existingExcludedPlayers[index].NormalizedName, StringComparison.Ordinal)
                || !entry.NormalizedTag.Equals(existingExcludedPlayers[index].NormalizedTag, StringComparison.Ordinal))
                .Any())
        {
            config.TranslateConfig.ExcludedPlayers = normalizedExcludedPlayers;
            changed = true;
        }

        var toxicDisplayMode = config.TranslateConfig.ToxicDisplayMode?.Trim().ToLowerInvariant();
        if (string.Equals(toxicDisplayMode, "raw", StringComparison.OrdinalIgnoreCase))
        {
            config.TranslateConfig.ToxicDisplayMode = "literal";
            changed = true;
            LogConfigMigration("Migrated ToxicDisplayMode raw -> literal. Use source for raw OCR text.");
        }
        else if (toxicDisplayMode is not ("hide" or "label" or "literal" or "source"))
        {
            config.TranslateConfig.ToxicDisplayMode = "label";
            changed = true;
        }
        else if (!string.Equals(config.TranslateConfig.ToxicDisplayMode, toxicDisplayMode, StringComparison.Ordinal))
        {
            config.TranslateConfig.ToxicDisplayMode = toxicDisplayMode;
            changed = true;
        }

        if (changed)
        {
            Save(config);
        }

        return config;
    }

    private void BackupBrokenConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return;
            }

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupPath = $"{ConfigPath}.broken.{timestamp}.bak";
            File.Copy(ConfigPath, backupPath, overwrite: false);
        }
        catch
        {
            // Ignore backup failures and fall back to a default config.
        }
    }

    private static void LogConfigMigration(string message)
    {
        try
        {
            AppLogService.AppendText(
                "config-migration.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Migration logging must never block config loading.
        }
    }

    private static bool IsLegacyAiEngine(string? engine)
    {
        return !string.IsNullOrWhiteSpace(engine)
            && (engine.Equals(TranslatorEngines.OpenAICompatible, StringComparison.OrdinalIgnoreCase)
                || engine.Equals("OpenAI Compatible", StringComparison.OrdinalIgnoreCase)
                || engine.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                || engine.Equals(TranslatorEngines.DeepSeekPreset, StringComparison.OrdinalIgnoreCase)
                || engine.Equals("DeepSeek", StringComparison.OrdinalIgnoreCase)
                || engine.Equals("DeepSeek Preset", StringComparison.OrdinalIgnoreCase)
                || engine.Equals(TranslatorEngines.Gemini, StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyLegacyAiDefaults(TranslateConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiBase))
        {
            config.ApiBase = config.TranslateEngine switch
            {
                var engine when engine.Equals(TranslatorEngines.DeepSeekPreset, StringComparison.OrdinalIgnoreCase)
                    || engine.Equals("DeepSeek", StringComparison.OrdinalIgnoreCase)
                    || engine.Equals("DeepSeek Preset", StringComparison.OrdinalIgnoreCase)
                    => TranslatorEngines.DeepSeekDefaultApiBase,
                var engine when engine.Equals(TranslatorEngines.Gemini, StringComparison.OrdinalIgnoreCase)
                    => TranslatorEngines.GeminiDefaultApiBase,
                _ => TranslatorEngines.OpenAICompatibleDefaultApiBase
            };
        }

        if (string.IsNullOrWhiteSpace(config.Model))
        {
            config.Model = config.TranslateEngine switch
            {
                var engine when engine.Equals(TranslatorEngines.DeepSeekPreset, StringComparison.OrdinalIgnoreCase)
                    || engine.Equals("DeepSeek", StringComparison.OrdinalIgnoreCase)
                    || engine.Equals("DeepSeek Preset", StringComparison.OrdinalIgnoreCase)
                    => TranslatorEngines.DeepSeekDefaultModel,
                var engine when engine.Equals(TranslatorEngines.Gemini, StringComparison.OrdinalIgnoreCase)
                    => TranslatorEngines.GeminiDefaultModel,
                _ => TranslatorEngines.OpenAICompatibleDefaultModel
            };
        }
    }

    private static bool ApplyStandardOcrPolicy(AppConfig config)
    {
        var changed = false;
        var ocrConfig = config.OcrConfig;

        if (ocrConfig.EnableAdaptiveDirtyRegionOcr)
        {
            ocrConfig.EnableAdaptiveDirtyRegionOcr = false;
            changed = true;
        }

        if (ocrConfig.EnableTextMaskDetection)
        {
            ocrConfig.EnableTextMaskDetection = false;
            changed = true;
        }

        if (ocrConfig.EnableFixedBottomOcr)
        {
            ocrConfig.EnableFixedBottomOcr = false;
            changed = true;
        }

        if (ocrConfig.SaveOcrDebugImages != config.EnableVerboseDiagnostics)
        {
            ocrConfig.SaveOcrDebugImages = config.EnableVerboseDiagnostics;
            changed = true;
        }

        if (Math.Abs(ocrConfig.ImageScale - 1.0) >= 0.0001)
        {
            ocrConfig.ImageScale = 1.0;
            changed = true;
        }

        if (Math.Abs(ocrConfig.Contrast - 1.0) >= 0.0001)
        {
            ocrConfig.Contrast = 1.0;
            changed = true;
        }

        if (ocrConfig.EnableSharpen)
        {
            ocrConfig.EnableSharpen = false;
            changed = true;
        }

        return changed;
    }
}
