using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public static class SettingsProfileService
{
    public const string SpeedStable = "stable";
    public const string SpeedBalanced = "balanced";
    public const string SpeedFast = "fast";

    public static void ApplyRecommendedDefaults(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        config.EnableAdvancedSettings = false;
        config.EnableVerboseDiagnostics = false;

        var ocr = config.OcrConfig;
        ocr.CaptureIntervalMs = 1200;
        ocr.OcrMode = OcrMode.Fast;
        ocr.OcrLanguage = OcrLanguages.Auto;
        ocr.OcrEngine = OcrEngines.PpOcrV5Multilingual;
        ocr.EnableRealtimeLowLatencyMode = true;
        ocr.EnableAdaptiveDirtyRegionOcr = false;
        ocr.EnableTextMaskDetection = false;
        ocr.EnableFixedBottomOcr = false;
        ocr.SaveOcrDebugImages = false;
        ocr.ShowOriginalBeforeTranslation = false;
        ocr.EnableLineDeduplication = true;
        ocr.ImageScale = 1.0;
        ocr.Contrast = 1.0;
        ocr.EnableSharpen = false;
        ocr.MinConfidence = 0.45;
        ocr.FullRescanIntervalMs = 8000;
        ocr.MaxDirtyRegionRatioBeforeFullScan = 0.35;
        ocr.DirtyRegionPaddingX = 12;
        ocr.DirtyRegionPaddingY = 8;
        ocr.DirtyLineBatchSize = 4;
        ocr.TextMaskDiffThreshold = 0.18;

        config.FilterConfig.FilterSystemMessages = true;
        config.FilterConfig.FilterPingMessages = true;
        config.FilterConfig.FilterKillMessages = true;
        config.FilterConfig.FilterPurchaseMessages = true;
        config.TranslateConfig.ToxicDisplayMode = "label";
    }

    public static string ResolveSpeedProfile(OcrConfig ocr)
    {
        if (ocr.CaptureIntervalMs <= 650 || string.Equals(ocr.OcrMode, OcrMode.Fast, StringComparison.OrdinalIgnoreCase))
        {
            return SpeedFast;
        }

        if (ocr.CaptureIntervalMs >= 1400 || string.Equals(ocr.OcrMode, OcrMode.Stable, StringComparison.OrdinalIgnoreCase))
        {
            return SpeedStable;
        }

        return SpeedBalanced;
    }

    public static void ApplySpeedProfile(OcrConfig ocr, string profile)
    {
        switch (profile)
        {
            case SpeedStable:
                ocr.CaptureIntervalMs = 1500;
                ocr.OcrMode = OcrMode.Stable;
                ocr.FullRescanIntervalMs = 6000;
                break;
            case SpeedFast:
                ocr.CaptureIntervalMs = 650;
                ocr.OcrMode = OcrMode.Fast;
                ocr.FullRescanIntervalMs = 10000;
                break;
            default:
                ocr.CaptureIntervalMs = 1200;
                ocr.OcrMode = OcrMode.Fast;
                ocr.FullRescanIntervalMs = 8000;
                break;
        }
    }
}
