using System.Text.Json.Serialization;

namespace LoLChatTranslator.Models;

public sealed class AppConfig
{
    public int ConfigSchemaVersion { get; set; } = 3;

    public string UiLanguage { get; set; } = "zh-Hans";

    public bool EnableVerboseDiagnostics { get; set; } = false;

    public bool EnableAdvancedSettings { get; set; } = false;

    public bool HasShownDevelopmentNotice { get; set; } = false;

    public bool HasShownOcrEnvironmentSetupPrompt { get; set; } = false;

    public bool HasCompletedOcrEnvironmentSetup { get; set; } = false;

    [JsonPropertyName("OCRConfig")]
    public OcrConfig OcrConfig { get; set; } = new();

    public TranslateConfig TranslateConfig { get; set; } = new();

    public FilterConfig FilterConfig { get; set; } = new();

    public OverlayConfig OverlayConfig { get; set; } = new();

    public HotkeyConfig HotkeyConfig { get; set; } = new();

    public static AppConfig CreateDefault() => new();
}

public sealed class OcrConfig
{
    public int RegionX { get; set; } = 20;

    public int RegionY { get; set; } = 520;

    public int RegionWidth { get; set; } = 620;

    public int RegionHeight { get; set; } = 260;

    public int CaptureIntervalMs { get; set; } = 1200;

    // Reserved for low-latency text-mask tuning; currently does not crop or change the selected OCR region.
    public int RealtimeDetectionLineCount { get; set; } = 5;

    // Reserved compatibility setting; the main auto OCR button still controls whether background OCR runs.
    public bool EnableRealtimeLowLatencyMode { get; set; } = true;

    // Reserved for future bottom-region experiments; currently not applied to avoid changing user-selected OCR input.
    public int RealtimeBottomHeight { get; set; } = 110;

    public bool EnableTextMaskDetection { get; set; } = false;

    // Experimental. The default OCR input must stay the full user-selected region.
    public bool EnableAdaptiveDirtyRegionOcr { get; set; } = false;

    public bool EnableFixedBottomOcr { get; set; } = false;

    public int DirtyRegionPaddingX { get; set; } = 12;

    public int DirtyRegionPaddingY { get; set; } = 8;

    public int FullRescanIntervalMs { get; set; } = 8000;

    public double MaxDirtyRegionRatioBeforeFullScan { get; set; } = 0.35;

    public int MinTextMaskChangedPixels { get; set; } = 30;

    public bool DirtyLineIncludeNeighborLines { get; set; } = true;

    public int DirtyLineBatchSize { get; set; } = 4;

    public int OcrTriggerMinIntervalMs { get; set; } = 300;

    public double TextMaskDiffThreshold { get; set; } = 0.18;

    public int OcrTimeoutMs { get; set; } = 3000;

    public bool SaveOcrDebugImages { get; set; } = false;

    // 当前未接入主翻译流程，保留为配置兼容；避免默认流程先输出原文再输出翻译造成重复显示。
    public bool ShowOriginalBeforeTranslation { get; set; } = false;

    public bool EnableLineDeduplication { get; set; } = true;

    public string OcrEngine { get; set; } = "PPOCRv5Multilingual";

    public string OcrLanguage { get; set; } = "auto";

    public string OcrMode { get; set; } = "fast";

    public string OcrEnvironmentDirectory { get; set; } = string.Empty;

    public double ImageScale { get; set; } = 1.0;

    public double Contrast { get; set; } = 1.0;

    public bool EnableSharpen { get; set; } = false;

    public double MinConfidence { get; set; } = 0.45;
}

public sealed class TranslateConfig
{
    public string SourceLanguage { get; set; } = "auto";

    public string TargetLanguage { get; set; } = "zh-Hans";

    public string OverlayInputTargetLanguage { get; set; } = "ocr-reverse";

    public string OverlayInputDefaultTargetLanguage { get; set; } = "en";

    public string TranslateEngine { get; set; } = "MyMemory免费翻译";

    public string ApiBase { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 20;

    public string ToxicDisplayMode { get; set; } = "label";

    public bool EnableOverlayInput { get; set; } = false;

    public bool ExcludePlayersEnabled { get; set; } = false;

    public List<ExcludedPlayerEntry> ExcludedPlayers { get; set; } = [];
}

public sealed class ExcludedPlayerEntry
{
    public string Name { get; set; } = string.Empty;

    public string Tag { get; set; } = string.Empty;

    public string RiotId { get; set; } = string.Empty;

    public string NormalizedName { get; set; } = string.Empty;

    public string NormalizedTag { get; set; } = string.Empty;

    public long CreatedAt { get; set; }
}

public sealed class FilterConfig
{
    public bool RemoveUsername { get; set; } = true;

    public bool RemoveChannelTag { get; set; } = true;

    public bool FilterSystemMessages { get; set; } = true;

    public bool FilterPingMessages { get; set; } = true;

    public bool FilterKillMessages { get; set; } = true;

    public bool FilterPurchaseMessages { get; set; } = true;
}

public sealed class OverlayConfig
{
    public double Opacity { get; set; } = 0.86;

    public double FontSize { get; set; } = 16;

    public int MaxLines { get; set; } = 8;

    public bool AlwaysOnTop { get; set; } = true;

    public bool ClickThrough { get; set; } = false;

    public bool ExcludeFromScreenCapture { get; set; } = true;

    public bool HideOverlayDuringCapture { get; set; } = true;

    public bool ShowSenderName { get; set; } = true;

    public string BackgroundColor { get; set; } = "#111827";

    public string InputBackgroundColor { get; set; } = "#F8FAFC";

    public string TeamHeaderColor { get; set; } = "#93C5FD";

    public string TeamTextColor { get; set; } = "#FFFFFF";

    public string AllHeaderColor { get; set; } = "#FCA5A5";

    public string AllTextColor { get; set; } = "#FFFFFF";

    public string PartyHeaderColor { get; set; } = "#C4B5FD";

    public string PartyTextColor { get; set; } = "#FFFFFF";

    public string UnknownHeaderColor { get; set; } = "#CBD5E1";

    public string UnknownTextColor { get; set; } = "#FFFFFF";

    public string SystemHeaderColor { get; set; } = "#FBBF24";

    public string SystemTextColor { get; set; } = "#FDE68A";
}

public sealed class HotkeyConfig
{
    public string ManualTranslateHotkey { get; set; } = "F8";

    public string ToggleAutoTranslateHotkey { get; set; } = "F9";

    public string TranslateClipboardHotkey { get; set; } = "Ctrl+Shift+T";

    public string OpenSettingsHotkey { get; set; } = "Ctrl+Shift+S";

    public string ReselectRegionHotkey { get; set; } = "Ctrl+Shift+R";

    public string PreviewRegionHotkey { get; set; } = "Ctrl+Shift+P";

    public string ToggleOverlayHotkey { get; set; } = "Ctrl+Shift+H";

    public string FocusOverlayInputHotkey { get; set; } = "Ctrl+Shift+I";
}
