using System.Text.Json;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Navigation;
using LoLChatTranslator.Models;
using LoLChatTranslator.Services;

namespace LoLChatTranslator;

public partial class SettingsWindow : Window
{
    private readonly ConfigService _configService;
    private readonly OcrDependencyInstallerService _ocrDependencyInstaller = new();
    private readonly GitHubUpdateService _updateService = new();
    private AppConfig _config;
    private bool _isLoadingControls;
    private bool _isSliderJumpDragging;
    private int _excludedPlayersStatusVersion;

    public SettingsWindow(AppConfig config, ConfigService configService)
    {
        InitializeComponent();

        _configService = configService;
        _config = CloneConfig(config);

        _isLoadingControls = true;
        LoadConfigToControls();
        _isLoadingControls = false;
    }

    public event EventHandler? ConfigSaved;

    public event EventHandler? OcrDependenciesInstalled;

    public event Action<AppConfig>? ConfigPreviewChanged;

    private void LoadConfigToControls()
    {
        RefreshRegionSummary();
        AdvancedOcrSettingsExpander.IsExpanded = _config.EnableAdvancedSettings;
        DiagnosticModeCheckBox.IsChecked = _config.EnableVerboseDiagnostics;
        SelectComboBoxByTag(AutoOcrSpeedComboBox, SettingsProfileService.ResolveSpeedProfile(_config.OcrConfig));
        CaptureIntervalTextBox.Text = _config.OcrConfig.CaptureIntervalMs.ToString();
        EnableRealtimeLowLatencyModeCheckBox.IsChecked = _config.OcrConfig.EnableRealtimeLowLatencyMode;
        EnableRealtimeLowLatencyModeCheckBox.IsEnabled = false;
        EnableAdaptiveDirtyRegionOcrCheckBox.IsChecked = _config.OcrConfig.EnableAdaptiveDirtyRegionOcr;
        EnableTextMaskDetectionCheckBox.IsChecked = _config.OcrConfig.EnableTextMaskDetection;
        EnableFixedBottomOcrCheckBox.IsChecked = _config.OcrConfig.EnableFixedBottomOcr;
        SaveOcrDebugImagesCheckBox.IsChecked = _config.OcrConfig.SaveOcrDebugImages;
        ShowOriginalBeforeTranslationCheckBox.IsChecked = _config.OcrConfig.ShowOriginalBeforeTranslation;
        ShowOriginalBeforeTranslationCheckBox.IsEnabled = false;
        EnableLineDeduplicationCheckBox.IsChecked = _config.OcrConfig.EnableLineDeduplication;
        SelectComboBoxByTag(OcrEngineComboBox, OcrEngines.Normalize(_config.OcrConfig.OcrEngine));
        SelectComboBoxByTag(OcrLanguageComboBox, OcrLanguages.Normalize(_config.OcrConfig.OcrLanguage));
        SelectComboBoxByTag(AdvancedOcrLanguageComboBox, OcrLanguages.Normalize(_config.OcrConfig.OcrLanguage));
        OcrEnvironmentDirectoryTextBox.Text = PythonEnvironmentService.ResolveOcrEnvironmentDirectory(_config.OcrConfig);
        SelectComboBoxByTag(OcrModeComboBox, OcrMode.Normalize(_config.OcrConfig.OcrMode));
        FullRescanIntervalTextBox.Text = _config.OcrConfig.FullRescanIntervalMs.ToString();
        MaxDirtyRegionRatioTextBox.Text = _config.OcrConfig.MaxDirtyRegionRatioBeforeFullScan.ToString("0.##");
        DirtyRegionPaddingXTextBox.Text = _config.OcrConfig.DirtyRegionPaddingX.ToString();
        DirtyRegionPaddingYTextBox.Text = _config.OcrConfig.DirtyRegionPaddingY.ToString();
        DirtyLineBatchSizeTextBox.Text = _config.OcrConfig.DirtyLineBatchSize.ToString();
        SelectComboBoxByTag(ImageScaleComboBox, _config.OcrConfig.ImageScale.ToString("0"));
        ContrastTextBox.Text = _config.OcrConfig.Contrast.ToString("0.##");
        EnableSharpenCheckBox.IsChecked = _config.OcrConfig.EnableSharpen;
        MinConfidenceTextBox.Text = _config.OcrConfig.MinConfidence.ToString("0.##");

        SelectComboBoxByTag(UiLanguageComboBox, _config.UiLanguage);
        SelectComboBoxByTag(SourceLanguageComboBox, _config.TranslateConfig.SourceLanguage);
        SelectComboBoxByTag(TargetLanguageComboBox, TranslatorLanguage.NormalizeTargetLanguage(_config.TranslateConfig.TargetLanguage));
        SelectComboBoxByTag(OverlayInputTargetLanguageComboBox, NormalizeOverlayInputTargetLanguage(_config.TranslateConfig.OverlayInputTargetLanguage));
        SelectComboBoxByTag(OverlayInputDefaultTargetLanguageComboBox, TranslatorLanguage.NormalizeTargetLanguage(_config.TranslateConfig.OverlayInputDefaultTargetLanguage));
        EnsureSourceAndTargetLanguagesDiffer();
        SelectComboBoxByTag(TranslateEngineComboBox, TranslatorEngines.Normalize(_config.TranslateConfig.TranslateEngine));
        SelectComboBoxByTag(ToxicDisplayModeComboBox, _config.TranslateConfig.ToxicDisplayMode);
        ExcludePlayersEnabledCheckBox.IsChecked = _config.TranslateConfig.ExcludePlayersEnabled;
        EnableOverlayInputCheckBox.IsChecked = _config.TranslateConfig.EnableOverlayInput;
        ApiBaseTextBox.Text = _config.TranslateConfig.ApiBase;
        ApiKeyPasswordBox.Password = TranslatorCredentialStore.GetApiKey(_config.TranslateConfig);
        ModelTextBox.Text = _config.TranslateConfig.Model;

        RemoveUsernameCheckBox.IsChecked = _config.FilterConfig.RemoveUsername;
        RemoveChannelTagCheckBox.IsChecked = _config.FilterConfig.RemoveChannelTag;
        FilterSystemMessagesCheckBox.IsChecked = _config.FilterConfig.FilterSystemMessages;
        FilterPingMessagesCheckBox.IsChecked = _config.FilterConfig.FilterPingMessages;
        FilterKillMessagesCheckBox.IsChecked = _config.FilterConfig.FilterKillMessages;
        FilterPurchaseMessagesCheckBox.IsChecked = _config.FilterConfig.FilterPurchaseMessages;

        OpacitySlider.Value = Clamp(_config.OverlayConfig.Opacity, 0.2, 1);
        FontSizeTextBox.Text = _config.OverlayConfig.FontSize.ToString("0.##");
        MaxLinesTextBox.Text = _config.OverlayConfig.MaxLines.ToString();
        ShowSenderNameCheckBox.IsChecked = _config.OverlayConfig.ShowSenderName;
        AlwaysOnTopCheckBox.IsChecked = _config.OverlayConfig.AlwaysOnTop;
        ClickThroughCheckBox.IsChecked = _config.OverlayConfig.ClickThrough;
        ExcludeFromScreenCaptureCheckBox.IsChecked = _config.OverlayConfig.ExcludeFromScreenCapture;
        HideOverlayDuringCaptureCheckBox.IsChecked = _config.OverlayConfig.HideOverlayDuringCapture;
        OverlayBackgroundColorTextBox.Text = _config.OverlayConfig.BackgroundColor;
        OverlayInputBackgroundColorTextBox.Text = _config.OverlayConfig.InputBackgroundColor;
        TeamHeaderColorTextBox.Text = _config.OverlayConfig.TeamHeaderColor;
        TeamTextColorTextBox.Text = _config.OverlayConfig.TeamTextColor;
        AllHeaderColorTextBox.Text = _config.OverlayConfig.AllHeaderColor;
        AllTextColorTextBox.Text = _config.OverlayConfig.AllTextColor;
        PartyHeaderColorTextBox.Text = _config.OverlayConfig.PartyHeaderColor;
        PartyTextColorTextBox.Text = _config.OverlayConfig.PartyTextColor;
        UnknownHeaderColorTextBox.Text = _config.OverlayConfig.UnknownHeaderColor;
        UnknownTextColorTextBox.Text = _config.OverlayConfig.UnknownTextColor;
        SystemHeaderColorTextBox.Text = _config.OverlayConfig.SystemHeaderColor;
        SystemTextColorTextBox.Text = _config.OverlayConfig.SystemTextColor;

        ManualTranslateHotkeyTextBox.Text = _config.HotkeyConfig.ManualTranslateHotkey;
        ToggleAutoTranslateHotkeyTextBox.Text = _config.HotkeyConfig.ToggleAutoTranslateHotkey;
        TranslateClipboardHotkeyTextBox.Text = _config.HotkeyConfig.TranslateClipboardHotkey;
        OpenSettingsHotkeyTextBox.Text = _config.HotkeyConfig.OpenSettingsHotkey;
        ReselectRegionHotkeyTextBox.Text = _config.HotkeyConfig.ReselectRegionHotkey;
        PreviewRegionHotkeyTextBox.Text = _config.HotkeyConfig.PreviewRegionHotkey;
        ToggleOverlayHotkeyTextBox.Text = _config.HotkeyConfig.ToggleOverlayHotkey;
        FocusOverlayInputHotkeyTextBox.Text = _config.HotkeyConfig.FocusOverlayInputHotkey;
        VersionTextBlock.Text = $"版本号 {GetCurrentVersionText()}";

        ApplySelectedTranslatorPresetToControls(overwriteExisting: false);
        RefreshExcludedPlayersList();
        UpdateExcludedPlayersVisibility();
        UpdateTranslatorSettingsVisibility();
        ApplyLocalization();
    }

    private void UpdateConfigFromControls()
    {
        _config.EnableAdvancedSettings = AdvancedOcrSettingsExpander.IsExpanded;
        _config.EnableVerboseDiagnostics = DiagnosticModeCheckBox.IsChecked == true;
        SettingsProfileService.ApplySpeedProfile(
            _config.OcrConfig,
            GetSelectedTag(AutoOcrSpeedComboBox, SettingsProfileService.SpeedBalanced));
        _config.OcrConfig.CaptureIntervalMs = ReadInt(CaptureIntervalTextBox, _config.OcrConfig.CaptureIntervalMs, 250, 5000);
        _config.OcrConfig.OcrEngine = OcrEngines.Normalize(GetSelectedTag(OcrEngineComboBox, OcrEngines.PpOcrV5Multilingual));
        _config.OcrConfig.OcrEnvironmentDirectory = PythonEnvironmentService.NormalizeOcrEnvironmentDirectory(OcrEnvironmentDirectoryTextBox.Text)
            ?? string.Empty;
        _config.OcrConfig.OcrLanguage = OcrLanguages.Normalize(_config.EnableAdvancedSettings
            ? GetSelectedTag(AdvancedOcrLanguageComboBox, GetSelectedTag(OcrLanguageComboBox, OcrLanguages.Auto))
            : GetSelectedTag(OcrLanguageComboBox, OcrLanguages.Auto));

        if (_config.EnableAdvancedSettings)
        {
            _config.OcrConfig.EnableRealtimeLowLatencyMode = EnableRealtimeLowLatencyModeCheckBox.IsChecked == true;
            _config.OcrConfig.EnableAdaptiveDirtyRegionOcr = EnableAdaptiveDirtyRegionOcrCheckBox.IsChecked == true;
            _config.OcrConfig.EnableTextMaskDetection = EnableTextMaskDetectionCheckBox.IsChecked == true;
            _config.OcrConfig.EnableFixedBottomOcr = EnableFixedBottomOcrCheckBox.IsChecked == true;
            _config.OcrConfig.SaveOcrDebugImages = _config.EnableVerboseDiagnostics || SaveOcrDebugImagesCheckBox.IsChecked == true;
            _config.OcrConfig.ShowOriginalBeforeTranslation = ShowOriginalBeforeTranslationCheckBox.IsChecked == true;
            _config.OcrConfig.EnableLineDeduplication = EnableLineDeduplicationCheckBox.IsChecked == true;
            _config.OcrConfig.OcrMode = OcrMode.Normalize(GetSelectedTag(OcrModeComboBox, OcrMode.Fast));
            _config.OcrConfig.FullRescanIntervalMs = ReadInt(FullRescanIntervalTextBox, _config.OcrConfig.FullRescanIntervalMs, 1200, 60000);
            _config.OcrConfig.MaxDirtyRegionRatioBeforeFullScan = ReadDouble(MaxDirtyRegionRatioTextBox, _config.OcrConfig.MaxDirtyRegionRatioBeforeFullScan, 0.05, 0.95);
            _config.OcrConfig.DirtyRegionPaddingX = ReadInt(DirtyRegionPaddingXTextBox, _config.OcrConfig.DirtyRegionPaddingX, 0, 80);
            _config.OcrConfig.DirtyRegionPaddingY = ReadInt(DirtyRegionPaddingYTextBox, _config.OcrConfig.DirtyRegionPaddingY, 0, 80);
            _config.OcrConfig.DirtyLineBatchSize = ReadInt(DirtyLineBatchSizeTextBox, _config.OcrConfig.DirtyLineBatchSize, 1, 16);
            _config.OcrConfig.ImageScale = ReadDoubleFromComboBox(ImageScaleComboBox, _config.OcrConfig.ImageScale, 1, 4);
            _config.OcrConfig.Contrast = ReadDouble(ContrastTextBox, _config.OcrConfig.Contrast, 0.5, 3);
            _config.OcrConfig.EnableSharpen = EnableSharpenCheckBox.IsChecked == true;
            _config.OcrConfig.MinConfidence = ReadDouble(MinConfidenceTextBox, _config.OcrConfig.MinConfidence, 0, 1);
        }
        else
        {
            _config.OcrConfig.EnableRealtimeLowLatencyMode = true;
            _config.OcrConfig.EnableAdaptiveDirtyRegionOcr = false;
            _config.OcrConfig.EnableTextMaskDetection = false;
            _config.OcrConfig.EnableFixedBottomOcr = false;
            _config.OcrConfig.SaveOcrDebugImages = _config.EnableVerboseDiagnostics;
            _config.OcrConfig.ShowOriginalBeforeTranslation = false;
            _config.OcrConfig.EnableLineDeduplication = true;
            _config.OcrConfig.ImageScale = 1.0;
            _config.OcrConfig.Contrast = 1.0;
            _config.OcrConfig.EnableSharpen = false;
        }

        _config.UiLanguage = GetSelectedTag(UiLanguageComboBox, "zh-Hans");
        _config.TranslateConfig.SourceLanguage = GetSelectedTag(SourceLanguageComboBox, "auto");
        _config.TranslateConfig.TargetLanguage = GetSelectedTag(TargetLanguageComboBox, "zh-Hans");
        _config.TranslateConfig.OverlayInputTargetLanguage = GetSelectedTag(OverlayInputTargetLanguageComboBox, "ocr-reverse");
        _config.TranslateConfig.OverlayInputDefaultTargetLanguage = GetSelectedTag(OverlayInputDefaultTargetLanguageComboBox, "en");
        EnsureSourceAndTargetLanguagesDiffer();
        _config.TranslateConfig.TranslateEngine = GetSelectedTag(TranslateEngineComboBox, TranslatorEngines.MyMemoryFree);
        _config.TranslateConfig.ToxicDisplayMode = GetSelectedTag(ToxicDisplayModeComboBox, "label");
        _config.TranslateConfig.ExcludePlayersEnabled = ExcludePlayersEnabledCheckBox.IsChecked == true;
        _config.TranslateConfig.ExcludedPlayers = PlayerExclusionService.NormalizeEntries(_config.TranslateConfig.ExcludedPlayers);
        _config.TranslateConfig.ApiBase = ApiBaseTextBox.Text.Trim();
        TranslatorCredentialStore.SetApiKey(_config.TranslateConfig, ApiKeyPasswordBox.Password);
        _config.TranslateConfig.Model = ModelTextBox.Text.Trim();
        _config.TranslateConfig.TimeoutSeconds = TranslatorEngines.ResolveTimeoutSeconds(_config.TranslateConfig);
        _config.TranslateConfig.EnableOverlayInput = EnableOverlayInputCheckBox.IsChecked == true;

        _config.FilterConfig.RemoveUsername = RemoveUsernameCheckBox.IsChecked == true;
        _config.FilterConfig.RemoveChannelTag = RemoveChannelTagCheckBox.IsChecked == true;
        _config.FilterConfig.FilterSystemMessages = FilterSystemMessagesCheckBox.IsChecked == true;
        _config.FilterConfig.FilterPingMessages = FilterPingMessagesCheckBox.IsChecked == true;
        _config.FilterConfig.FilterKillMessages = FilterKillMessagesCheckBox.IsChecked == true;
        _config.FilterConfig.FilterPurchaseMessages = FilterPurchaseMessagesCheckBox.IsChecked == true;

        UpdateOverlayConfigFromControls(_config);

        _config.HotkeyConfig.ManualTranslateHotkey = ManualTranslateHotkeyTextBox.Text.Trim();
        _config.HotkeyConfig.ToggleAutoTranslateHotkey = ToggleAutoTranslateHotkeyTextBox.Text.Trim();
        _config.HotkeyConfig.TranslateClipboardHotkey = TranslateClipboardHotkeyTextBox.Text.Trim();
        _config.HotkeyConfig.OpenSettingsHotkey = OpenSettingsHotkeyTextBox.Text.Trim();
        _config.HotkeyConfig.ReselectRegionHotkey = ReselectRegionHotkeyTextBox.Text.Trim();
        _config.HotkeyConfig.PreviewRegionHotkey = PreviewRegionHotkeyTextBox.Text.Trim();
        _config.HotkeyConfig.ToggleOverlayHotkey = ToggleOverlayHotkeyTextBox.Text.Trim();
        _config.HotkeyConfig.FocusOverlayInputHotkey = FocusOverlayInputHotkeyTextBox.Text.Trim();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateConfigFromControls();
        _configService.Save(_config);
        ConfigSaved?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateConfigFromControls();
        _configService.Save(_config);
        ConfigSaved?.Invoke(this, EventArgs.Empty);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RestoreDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsProfileService.ApplyRecommendedDefaults(_config);
        _isLoadingControls = true;
        LoadConfigToControls();
        _isLoadingControls = false;
        RaiseDisplayPreviewChanged();
    }

    private void BrowseOcrEnvironmentDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = UiTextLocalizer.Text(
                _config.UiLanguage,
                "选择 PP-OCRv5 OCR 环境安装位置",
                "選擇 PP-OCRv5 OCR 環境安裝位置",
                "Choose the PP-OCRv5 OCR environment location",
                "PP-OCRv5 OCR 환경 설치 위치 선택",
                "PP-OCRv5 OCR環境の場所を選択",
                "Chọn vị trí môi trường OCR PP-OCRv5"),
            InitialDirectory = Directory.Exists(OcrEnvironmentDirectoryTextBox.Text)
                ? OcrEnvironmentDirectoryTextBox.Text
                : PythonEnvironmentService.DefaultOcrEnvironmentDirectory
        };

        if (dialog.ShowDialog(this) == true)
        {
            OcrEnvironmentDirectoryTextBox.Text = dialog.FolderName;
        }
    }

    private void DisplayPreviewControl_Changed(object sender, RoutedEventArgs e)
    {
        RaiseDisplayPreviewChanged();
    }

    private void DisplayPreviewControl_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RaiseDisplayPreviewChanged();
    }

    private void TranslateEngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoadingControls)
        {
            ApplySelectedTranslatorPresetToControls(overwriteExisting: true);
        }

        UpdateTranslatorSettingsVisibility();
    }

    private void ExcludePlayersEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isLoadingControls)
        {
            _config.TranslateConfig.ExcludePlayersEnabled = ExcludePlayersEnabledCheckBox.IsChecked == true;
        }

        UpdateExcludedPlayersVisibility();
    }

    private void AddExcludedPlayerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_config.TranslateConfig.ExcludedPlayers.Count >= PlayerExclusionService.MaxExcludedPlayers)
        {
            SetExcludedPlayersStatus(LocalizeExcludePlayersText("MaxPlayers"), isError: true);
            return;
        }

        ExcludedPlayerNameTextBox.Text = string.Empty;
        ExcludedPlayerTagTextBox.Text = string.Empty;
        AddExcludedPlayerPanel.Visibility = Visibility.Visible;
        SetExcludedPlayersStatus(string.Empty);
        ExcludedPlayerNameTextBox.Focus();
    }

    private void CancelAddExcludedPlayerButton_Click(object sender, RoutedEventArgs e)
    {
        AddExcludedPlayerPanel.Visibility = Visibility.Collapsed;
        SetExcludedPlayersStatus(string.Empty);
    }

    private void ConfirmAddExcludedPlayerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_config.TranslateConfig.ExcludedPlayers.Count >= PlayerExclusionService.MaxExcludedPlayers)
        {
            SetExcludedPlayersStatus(LocalizeExcludePlayersText("MaxPlayers"), isError: true);
            return;
        }

        var name = ExcludedPlayerNameTextBox.Text.Trim();
        var tag = ExcludedPlayerTagTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SetExcludedPlayersStatus(LocalizeExcludePlayersText("NameRequired"), isError: true);
            ExcludedPlayerNameTextBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            SetExcludedPlayersStatus(LocalizeExcludePlayersText("TagRequired"), isError: true);
            ExcludedPlayerTagTextBox.Focus();
            return;
        }

        var entry = PlayerExclusionService.CreateEntry(name, tag);
        if (string.IsNullOrWhiteSpace(entry.NormalizedName))
        {
            SetExcludedPlayersStatus(LocalizeExcludePlayersText("NameRequired"), isError: true);
            ExcludedPlayerNameTextBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.NormalizedTag))
        {
            SetExcludedPlayersStatus(LocalizeExcludePlayersText("TagRequired"), isError: true);
            ExcludedPlayerTagTextBox.Focus();
            return;
        }

        var newKey = PlayerExclusionService.NormalizeRiotId(entry.Name, entry.Tag);
        var duplicate = _config.TranslateConfig.ExcludedPlayers.Any(player =>
            PlayerExclusionService.NormalizeRiotId(player.Name, player.Tag).Equals(newKey, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            SetExcludedPlayersStatus(LocalizeExcludePlayersText("Duplicate"), isError: true);
            return;
        }

        _config.TranslateConfig.ExcludedPlayers.Add(entry);
        AddExcludedPlayerPanel.Visibility = Visibility.Collapsed;
        RefreshExcludedPlayersList();
        SetExcludedPlayersStatus(LocalizeExcludePlayersText("Added"));
    }

    private void RemoveExcludedPlayerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ExcludedPlayerEntry player })
        {
            return;
        }

        _config.TranslateConfig.ExcludedPlayers.Remove(player);
        RefreshExcludedPlayersList();
        SetExcludedPlayersStatus(LocalizeExcludePlayersText("Removed"), autoClearAfter: TimeSpan.FromSeconds(3));
    }

    private async void TestTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateConfigFromControls();
        TestTranslationButton.IsEnabled = false;
        TestTranslationStatusTextBlock.Foreground = System.Windows.Media.Brushes.DimGray;
        TestTranslationStatusTextBlock.Text = UiTextLocalizer.Text(
            _config.UiLanguage,
            "正在测试...",
            "正在測試...",
            "Testing...",
            "테스트 중...",
            "テスト中...",
            "Đang kiểm tra...");

        try
        {
            var translateService = new TranslateService(_config);
            var translatedText = await translateService.TranslateAsync(
                "mid no flash",
                _config.TranslateConfig.TargetLanguage,
                "en");

            var isError = TranslatorErrorSanitizer.IsErrorResult(translatedText);
            TestTranslationStatusTextBlock.Foreground = isError
                ? System.Windows.Media.Brushes.Firebrick
                : System.Windows.Media.Brushes.SeaGreen;
            TestTranslationStatusTextBlock.Text = isError
                ? translatedText
                : UiTextLocalizer.Text(_config.UiLanguage, "成功连接", "連線成功", "Connection succeeded", "연결 성공", "接続成功", "Kết nối thành công");
        }
        finally
        {
            TestTranslationButton.IsEnabled = true;
        }
    }

    private void UiLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingControls || !IsLoaded)
        {
            return;
        }

        _config.UiLanguage = GetSelectedTag(UiLanguageComboBox, "zh-Hans");
        ApplyLocalization();
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingControls || !IsLoaded)
        {
            return;
        }

        EnsureSourceAndTargetLanguagesDiffer();
    }

    private void EnsureSourceAndTargetLanguagesDiffer()
    {
        var sourceLanguage = GetSelectedTag(SourceLanguageComboBox, "auto");
        var targetLanguage = GetSelectedTag(TargetLanguageComboBox, "zh-Hans");
        if (sourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || !sourceLanguage.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var preferredTarget = TranslatorLanguage.NormalizeTargetLanguage(_config.UiLanguage);
        if (preferredTarget.Equals(sourceLanguage, StringComparison.OrdinalIgnoreCase))
        {
            preferredTarget = TranslatorLanguage.SupportedTargetLanguages
                .FirstOrDefault(language => !language.Equals(sourceLanguage, StringComparison.OrdinalIgnoreCase))
                ?? "zh-Hans";
        }

        SelectComboBoxByTag(TargetLanguageComboBox, preferredTarget);
        _config.TranslateConfig.TargetLanguage = preferredTarget;
    }

    private void ProjectLinkHyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        CheckUpdatesStatusTextBlock.Foreground = System.Windows.Media.Brushes.DimGray;
        CheckUpdatesStatusTextBlock.Text = UiTextLocalizer.Text(
            _config.UiLanguage,
            "正在检查更新...",
            "正在檢查更新...",
            "Checking for updates...",
            "업데이트 확인 중...",
            "更新を確認中...",
            "Đang kiểm tra cập nhật...");

        try
        {
            var result = await _updateService.CheckLatestReleaseAsync(GetCurrentVersion());
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                CheckUpdatesStatusTextBlock.Foreground = System.Windows.Media.Brushes.Firebrick;
                CheckUpdatesStatusTextBlock.Text = result.ErrorMessage;
                return;
            }

            if (!result.HasUpdate || result.LatestVersion is null || string.IsNullOrWhiteSpace(result.DownloadUrl))
            {
                CheckUpdatesStatusTextBlock.Foreground = System.Windows.Media.Brushes.SeaGreen;
                CheckUpdatesStatusTextBlock.Text = UiTextLocalizer.Text(
                    _config.UiLanguage,
                    "当前已是最新版本。",
                    "目前已是最新版本。",
                    "You are already on the latest version.",
                    "현재 최신 버전입니다.",
                    "現在最新バージョンです。",
                    "Bạn đang dùng phiên bản mới nhất.");
                return;
            }

            CheckUpdatesStatusTextBlock.Text = string.Empty;
            var updateWindow = new UpdateAvailableWindow(FormatVersion(result.LatestVersion), _config.UiLanguage)
            {
                Owner = this
            };

            if (updateWindow.ShowDialog() == true)
            {
                OpenUrl(result.DownloadUrl);
            }
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void PickColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string targetTextBoxName }
            || FindName(targetTextBoxName) is not TextBox targetTextBox)
        {
            return;
        }

        var picker = new ColorPickerWindow(targetTextBox.Text, _config.UiLanguage)
        {
            Owner = this
        };

        if (picker.ShowDialog() == true)
        {
            targetTextBox.Text = picker.SelectedColorHex;
        }
    }

    private void SliderJumpToPoint_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || e.OriginalSource is Thumb)
        {
            return;
        }

        _isSliderJumpDragging = true;
        UpdateSliderValueFromMouse(slider, e);
        slider.CaptureMouse();
        e.Handled = true;
    }

    private void SliderJumpToPoint_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSliderJumpDragging || sender is not Slider slider || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        UpdateSliderValueFromMouse(slider, e);
        e.Handled = true;
    }

    private void SliderJumpToPoint_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSliderJumpDragging || sender is not Slider slider)
        {
            return;
        }

        _isSliderJumpDragging = false;
        slider.ReleaseMouseCapture();
        e.Handled = true;
    }

    private static void UpdateSliderValueFromMouse(Slider slider, MouseEventArgs e)
    {
        var track = slider.Template.FindName("PART_Track", slider) as Track;
        var target = track as FrameworkElement ?? slider;
        var width = Math.Max(1, target.ActualWidth);
        var ratio = Math.Clamp(e.GetPosition(target).X / width, 0, 1);
        if (slider.FlowDirection == FlowDirection.RightToLeft)
        {
            ratio = 1 - ratio;
        }

        slider.Value = slider.Minimum + (slider.Maximum - slider.Minimum) * ratio;
    }

    private void RaiseDisplayPreviewChanged()
    {
        if (_isLoadingControls || !IsLoaded)
        {
            return;
        }

        var previewConfig = CloneConfig(_config);
        previewConfig.UiLanguage = GetSelectedTag(UiLanguageComboBox, previewConfig.UiLanguage);
        previewConfig.TranslateConfig.EnableOverlayInput = EnableOverlayInputCheckBox.IsChecked == true;
        UpdateOverlayConfigFromControls(previewConfig);
        ConfigPreviewChanged?.Invoke(previewConfig);
    }

    private void UpdateOverlayConfigFromControls(AppConfig targetConfig)
    {
        targetConfig.OverlayConfig.Opacity = Clamp(OpacitySlider.Value, 0.2, 1);
        targetConfig.OverlayConfig.FontSize = ReadDouble(FontSizeTextBox, targetConfig.OverlayConfig.FontSize, 10, 36);
        targetConfig.OverlayConfig.MaxLines = ReadInt(MaxLinesTextBox, targetConfig.OverlayConfig.MaxLines, 1);
        targetConfig.OverlayConfig.ShowSenderName = ShowSenderNameCheckBox.IsChecked != false;
        targetConfig.OverlayConfig.AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true;
        targetConfig.OverlayConfig.ClickThrough = ClickThroughCheckBox.IsChecked == true;
        targetConfig.OverlayConfig.ExcludeFromScreenCapture = ExcludeFromScreenCaptureCheckBox.IsChecked == true;
        targetConfig.OverlayConfig.HideOverlayDuringCapture = HideOverlayDuringCaptureCheckBox.IsChecked == true;
        targetConfig.OverlayConfig.BackgroundColor = ReadColor(OverlayBackgroundColorTextBox, targetConfig.OverlayConfig.BackgroundColor);
        targetConfig.OverlayConfig.InputBackgroundColor = ReadColor(OverlayInputBackgroundColorTextBox, targetConfig.OverlayConfig.InputBackgroundColor);
        targetConfig.OverlayConfig.TeamHeaderColor = ReadColor(TeamHeaderColorTextBox, targetConfig.OverlayConfig.TeamHeaderColor);
        targetConfig.OverlayConfig.TeamTextColor = ReadColor(TeamTextColorTextBox, targetConfig.OverlayConfig.TeamTextColor);
        targetConfig.OverlayConfig.AllHeaderColor = ReadColor(AllHeaderColorTextBox, targetConfig.OverlayConfig.AllHeaderColor);
        targetConfig.OverlayConfig.AllTextColor = ReadColor(AllTextColorTextBox, targetConfig.OverlayConfig.AllTextColor);
        targetConfig.OverlayConfig.PartyHeaderColor = ReadColor(PartyHeaderColorTextBox, targetConfig.OverlayConfig.PartyHeaderColor);
        targetConfig.OverlayConfig.PartyTextColor = ReadColor(PartyTextColorTextBox, targetConfig.OverlayConfig.PartyTextColor);
        targetConfig.OverlayConfig.UnknownHeaderColor = ReadColor(UnknownHeaderColorTextBox, targetConfig.OverlayConfig.UnknownHeaderColor);
        targetConfig.OverlayConfig.UnknownTextColor = ReadColor(UnknownTextColorTextBox, targetConfig.OverlayConfig.UnknownTextColor);
        targetConfig.OverlayConfig.SystemHeaderColor = ReadColor(SystemHeaderColorTextBox, targetConfig.OverlayConfig.SystemHeaderColor);
        targetConfig.OverlayConfig.SystemTextColor = ReadColor(SystemTextColorTextBox, targetConfig.OverlayConfig.SystemTextColor);
    }

    private void UpdateTranslatorSettingsVisibility()
    {
        if (AiTranslatorSettingsPanel is null || TranslateEngineComboBox is null)
        {
            return;
        }

        var engine = GetSelectedTag(TranslateEngineComboBox, TranslatorEngines.MyMemoryFree);
        var usesApiSettings = TranslatorEngines.UsesApiSettings(engine);

        AiTranslatorSettingsPanel.Visibility = usesApiSettings ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyRow.Visibility = TranslatorEngines.RequiresApiKey(engine) ? Visibility.Visible : Visibility.Collapsed;
        MyMemoryTranslatorNoticeTextBlock.Visibility = TranslatorEngines.IsMyMemoryFreeTranslator(engine)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateExcludedPlayersVisibility()
    {
        if (ExcludedPlayersPanel is null)
        {
            return;
        }

        var enabled = ExcludePlayersEnabledCheckBox.IsChecked == true;
        ExcludedPlayersPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled)
        {
            AddExcludedPlayerPanel.Visibility = Visibility.Collapsed;
        }

        UpdateExcludedPlayersLimitState();
    }

    private void RefreshExcludedPlayersList()
    {
        _config.TranslateConfig.ExcludedPlayers = PlayerExclusionService.NormalizeEntries(_config.TranslateConfig.ExcludedPlayers);
        ExcludedPlayersItemsControl.ItemsSource = null;
        ExcludedPlayersItemsControl.ItemsSource = _config.TranslateConfig.ExcludedPlayers;
        UpdateExcludedPlayersLimitState();
    }

    private void UpdateExcludedPlayersLimitState()
    {
        if (AddExcludedPlayerButton is null)
        {
            return;
        }

        var reachedLimit = _config.TranslateConfig.ExcludedPlayers.Count >= PlayerExclusionService.MaxExcludedPlayers;
        AddExcludedPlayerButton.IsEnabled = !reachedLimit;
        if (reachedLimit)
        {
            SetExcludedPlayersStatus(LocalizeExcludePlayersText("MaxPlayers"), isError: true);
        }
        else if (ExcludedPlayersStatusTextBlock.Text.Equals(LocalizeExcludePlayersText("MaxPlayers"), StringComparison.Ordinal))
        {
            SetExcludedPlayersStatus(string.Empty);
        }
    }

    private void SetExcludedPlayersStatus(string message, bool isError = false, TimeSpan? autoClearAfter = null)
    {
        var version = ++_excludedPlayersStatusVersion;
        ExcludedPlayersStatusTextBlock.Text = message;
        ExcludedPlayersStatusTextBlock.Foreground = isError
            ? System.Windows.Media.Brushes.Firebrick
            : System.Windows.Media.Brushes.DimGray;

        if (autoClearAfter.HasValue && !string.IsNullOrWhiteSpace(message))
        {
            _ = ClearExcludedPlayersStatusAfterDelayAsync(version, autoClearAfter.Value);
        }
    }

    private async Task ClearExcludedPlayersStatusAfterDelayAsync(int version, TimeSpan delay)
    {
        await Task.Delay(delay);
        if (_excludedPlayersStatusVersion == version)
        {
            ExcludedPlayersStatusTextBlock.Text = string.Empty;
        }
    }

    private string LocalizeExcludePlayersText(string key)
    {
        var language = LocalizationService.NormalizeLanguage(_config.UiLanguage);
        return (language, key) switch
        {
            ("zh-Hant", "MaxPlayers") => "最多只能排除 50 個玩家",
            ("en", "MaxPlayers") => "You can exclude up to 50 players.",
            ("ko", "MaxPlayers") => "최대 50명의 플레이어만 제외할 수 있습니다.",
            ("ja", "MaxPlayers") => "除外できるプレイヤーは最大 50 人です。",
            ("vi", "MaxPlayers") => "Chỉ có thể loại trừ tối đa 50 người chơi.",
            (_, "MaxPlayers") => "最多只能排除 50 个玩家",

            ("zh-Hant", "Duplicate") => "該玩家已在排除列表中",
            ("en", "Duplicate") => "This player is already in the exclusion list.",
            ("ko", "Duplicate") => "이 플레이어는 이미 제외 목록에 있습니다.",
            ("ja", "Duplicate") => "このプレイヤーはすでに除外リストにあります。",
            ("vi", "Duplicate") => "Người chơi này đã có trong danh sách loại trừ.",
            (_, "Duplicate") => "该玩家已在排除列表中",

            ("zh-Hant", "NameRequired") => "玩家名稱不能為空",
            ("en", "NameRequired") => "Player name cannot be empty.",
            ("ko", "NameRequired") => "플레이어 이름은 비워 둘 수 없습니다.",
            ("ja", "NameRequired") => "プレイヤー名は空にできません。",
            ("vi", "NameRequired") => "Tên người chơi không được để trống.",
            (_, "NameRequired") => "玩家名称不能为空",

            ("zh-Hant", "TagRequired") => "玩家編號不能為空",
            ("en", "TagRequired") => "Player tag cannot be empty.",
            ("ko", "TagRequired") => "플레이어 태그는 비워 둘 수 없습니다.",
            ("ja", "TagRequired") => "プレイヤータグは空にできません。",
            ("vi", "TagRequired") => "Mã người chơi không được để trống.",
            (_, "TagRequired") => "玩家编号不能为空",

            ("zh-Hant", "Added") => "已加入排除列表",
            ("en", "Added") => "Added to the exclusion list.",
            ("ko", "Added") => "제외 목록에 추가했습니다.",
            ("ja", "Added") => "除外リストに追加しました。",
            ("vi", "Added") => "Đã thêm vào danh sách loại trừ.",
            (_, "Added") => "已加入排除列表",

            ("zh-Hant", "Removed") => "已從排除列表刪除",
            ("en", "Removed") => "Removed from the exclusion list.",
            ("ko", "Removed") => "제외 목록에서 삭제했습니다.",
            ("ja", "Removed") => "除外リストから削除しました。",
            ("vi", "Removed") => "Đã xóa khỏi danh sách loại trừ.",
            (_, "Removed") => "已从排除列表删除",

            _ => string.Empty
        };
    }

    private void ApplySelectedTranslatorPresetToControls(bool overwriteExisting)
    {
        if (TranslateEngineComboBox is null || ApiBaseTextBox is null || ModelTextBox is null)
        {
            return;
        }

        var engine = TranslatorEngines.Normalize(GetSelectedTag(TranslateEngineComboBox, TranslatorEngines.MyMemoryFree));
        var defaultApiBase = engine switch
        {
            TranslatorEngines.AiApi => TranslatorEngines.OpenAICompatibleDefaultApiBase,
            TranslatorEngines.Ollama => TranslatorEngines.OllamaDefaultApiBase,
            _ => string.Empty
        };
        var defaultModel = engine switch
        {
            TranslatorEngines.AiApi => TranslatorEngines.OpenAICompatibleDefaultModel,
            TranslatorEngines.Ollama => TranslatorEngines.OllamaDefaultModel,
            _ => string.Empty
        };

        if (!string.IsNullOrWhiteSpace(defaultApiBase)
            && (overwriteExisting || string.IsNullOrWhiteSpace(ApiBaseTextBox.Text) || IsKnownDefaultApiBase(ApiBaseTextBox.Text)))
        {
            ApiBaseTextBox.Text = defaultApiBase;
        }

        if (!string.IsNullOrWhiteSpace(defaultModel)
            && (overwriteExisting || string.IsNullOrWhiteSpace(ModelTextBox.Text) || IsKnownDefaultModel(ModelTextBox.Text)))
        {
            ModelTextBox.Text = defaultModel;
        }
    }

    private void ApplyLocalization()
    {
        var language = LocalizationService.NormalizeLanguage(_config.UiLanguage);

        Title = LocalizationService.T(language, "WindowSettings");
        UiTextLocalizer.ApplyTo(this, language);
        RestoreDefaultButton.Content = language switch
        {
            "zh-Hant" => "恢復推薦設定",
            "en" => "Restore Recommended",
            "ko" => "권장 설정 복원",
            "ja" => "推奨設定に戻す",
            "vi" => "Khôi phục khuyến nghị",
            _ => "恢复推荐设置"
        };
        CancelButton.Content = language switch
        {
            "zh-Hant" => "取消",
            "en" => "Cancel",
            "ko" => "취소",
            "ja" => "キャンセル",
            "vi" => "Hủy",
            _ => "取消"
        };
        ApplyButton.Content = language switch
        {
            "zh-Hant" => "套用",
            "en" => "Apply",
            "ko" => "적용",
            "ja" => "適用",
            "vi" => "Áp dụng",
            _ => "应用"
        };
        SaveButton.Content = language switch
        {
            "zh-Hant" => "儲存",
            "en" => "Save",
            "ko" => "저장",
            "ja" => "保存",
            "vi" => "Lưu",
            _ => "保存"
        };

        OcrTabItem.Header = language switch
        {
            "zh-Hant" => "OCR 設定",
            "en" => "OCR",
            "ko" => "OCR 설정",
            "ja" => "OCR 設定",
            "vi" => "OCR",
            _ => "OCR 设置"
        };
        TranslateTabItem.Header = language switch
        {
            "zh-Hant" => "翻譯設定",
            "en" => "Translation",
            "ko" => "번역 설정",
            "ja" => "翻訳設定",
            "vi" => "Dịch",
            _ => "翻译设置"
        };
        FilterTabItem.Header = language switch
        {
            "zh-Hant" => "過濾設定",
            "en" => "Filters",
            "ko" => "필터",
            "ja" => "フィルター",
            "vi" => "Bộ lọc",
            _ => "过滤设置"
        };
        DisplayTabItem.Header = language switch
        {
            "zh-Hant" => "顯示設定",
            "en" => "Display",
            "ko" => "표시",
            "ja" => "表示",
            "vi" => "Hiển thị",
            _ => "显示设置"
        };
        HotkeyTabItem.Header = language switch
        {
            "zh-Hant" => "快捷鍵設定",
            "en" => "Hotkeys",
            "ko" => "단축키",
            "ja" => "ホットキー",
            "vi" => "Phím tắt",
            _ => "快捷键设置"
        };
        AboutTabItem.Header = language switch
        {
            "zh-Hant" => "關於",
            "en" => "About",
            "ko" => "정보",
            "ja" => "情報",
            "vi" => "Giới thiệu",
            _ => "关于"
        };

        UiLanguageLabel.Text = LocalizationService.T(language, "UiLanguage");
        SourceLanguageLabel.Text = LocalizationService.T(language, "SourceLanguage");
        TargetLanguageLabel.Text = LocalizationService.T(language, "TargetLanguage");
        TranslateServiceLabel.Text = language switch
        {
            "zh-Hant" => "翻譯服務",
            "en" => "Translation Service",
            "ko" => "번역 서비스",
            "ja" => "翻訳サービス",
            "vi" => "Dịch vụ dịch",
            _ => "翻译服务"
        };
        ToxicDisplayModeLabel.Text = language switch
        {
            "zh-Hant" => "毒性內容顯示",
            "en" => "Toxic Display",
            "ko" => "유해 표현 표시",
            "ja" => "有害表現表示",
            "vi" => "Hiển thị độc hại",
            _ => "毒性内容显示"
        };
        ExcludePlayersEnabledCheckBox.Content = language switch
        {
            "zh-Hant" => "排除玩家",
            "en" => "Exclude Players",
            "ko" => "플레이어 제외",
            "ja" => "プレイヤーを除外",
            "vi" => "Loại trừ người chơi",
            _ => "排除玩家"
        };
        AddExcludedPlayerButton.Content = language switch
        {
            "zh-Hant" => "新增玩家",
            "en" => "Add Player",
            "ko" => "플레이어 추가",
            "ja" => "プレイヤー追加",
            "vi" => "Thêm người chơi",
            _ => "新增玩家"
        };
        ExcludedPlayerNameLabel.Text = language switch
        {
            "zh-Hant" => "玩家名稱",
            "en" => "Player Name",
            "ko" => "플레이어 이름",
            "ja" => "プレイヤー名",
            "vi" => "Tên người chơi",
            _ => "玩家名称"
        };
        ExcludedPlayerTagLabel.Text = language switch
        {
            "zh-Hant" => "玩家編號",
            "en" => "Player Tag",
            "ko" => "플레이어 태그",
            "ja" => "プレイヤータグ",
            "vi" => "Mã người chơi",
            _ => "玩家编号"
        };
        CancelAddExcludedPlayerButton.Content = language switch
        {
            "zh-Hant" => "取消",
            "en" => "Cancel",
            "ko" => "취소",
            "ja" => "キャンセル",
            "vi" => "Hủy",
            _ => "取消"
        };
        ConfirmAddExcludedPlayerButton.Content = language switch
        {
            "zh-Hant" => "新增",
            "en" => "Add",
            "ko" => "추가",
            "ja" => "追加",
            "vi" => "Thêm",
            _ => "添加"
        };
        EnableOverlayInputCheckBox.Content = language switch
        {
            "zh-Hant" => "為懸浮窗開啟輸入框",
            "en" => "Enable input box in overlay",
            "ko" => "오버레이 입력창 사용",
            "ja" => "オーバーレイ入力欄を有効化",
            "vi" => "Bật ô nhập trong cửa sổ nổi",
            _ => "为悬浮窗开启输入框"
        };
        OverlayInputTargetLanguageLabel.Text = language switch
        {
            "zh-Hant" => "懸浮窗輸入輸出語言",
            "en" => "Overlay Input Output Language",
            "ko" => "오버레이 입력 출력 언어",
            "ja" => "オーバーレイ入力の出力言語",
            "vi" => "Ngôn ngữ đầu ra ô nhập nổi",
            _ => "悬浮窗输入输出语言"
        };
        OverlayInputDefaultTargetLanguageLabel.Text = language switch
        {
            "zh-Hant" => "自動失敗預設語言",
            "en" => "Auto Fallback Language",
            "ko" => "자동 실패 시 기본 언어",
            "ja" => "自動失敗時の既定言語",
            "vi" => "Ngôn ngữ dự phòng tự động",
            _ => "自动失败默认语言"
        };
        LocalizeToxicDisplayModeComboBox(language);
        ApiBaseLabel.Text = "API Base";
        ApiKeyLabel.Text = "API Key";
        ModelLabel.Text = "Model";
        ProjectLinkLabel.Text = language switch
        {
            "zh-Hant" => "專案連結",
            "en" => "Project Link",
            "ko" => "프로젝트 링크",
            "ja" => "プロジェクトリンク",
            "vi" => "Liên kết dự án",
            _ => "项目链接"
        };
        CheckUpdatesButton.Content = language switch
        {
            "zh-Hant" => "檢查更新",
            "en" => "Check Updates",
            "ko" => "업데이트 확인",
            "ja" => "更新を確認",
            "vi" => "Kiểm tra cập nhật",
            _ => "检查更新"
        };
        InstallOcrDependenciesButton.Content = language switch
        {
            "zh-Hant" => "偵測/安裝 PP-OCRv5 OCR 環境",
            "en" => "Check / Install PP-OCRv5 OCR",
            "ko" => "PP-OCRv5 OCR 환경 확인/설치",
            "ja" => "PP-OCRv5 OCR環境を確認/インストール",
            "vi" => "Kiểm tra / cài OCR PP-OCRv5",
            _ => "检测/安装 PP-OCRv5 OCR 环境"
        };
        DeleteOcrEnvironmentButton.Content = language switch
        {
            "zh-Hant" => "刪除 PP-OCRv5 OCR 環境",
            "en" => "Delete PP-OCRv5 OCR Environment",
            "ko" => "PP-OCRv5 OCR 환경 삭제",
            "ja" => "PP-OCRv5 OCR環境を削除",
            "vi" => "Xóa môi trường OCR PP-OCRv5",
            _ => "删除 PP-OCRv5 OCR 环境"
        };
        VersionTextBlock.Text = language switch
        {
            "zh-Hant" => $"版本號 {GetCurrentVersionText()}",
            "en" => $"Version {GetCurrentVersionText()}",
            "ko" => $"버전 {GetCurrentVersionText()}",
            "ja" => $"バージョン {GetCurrentVersionText()}",
            "vi" => $"Phiên bản {GetCurrentVersionText()}",
            _ => $"版本号 {GetCurrentVersionText()}"
        };
        DevelopmentNoticeTextBlock.Text = language switch
        {
            "zh-Hant" => "目前處於開發階段，可能存在不穩定情況！",
            "en" => "This app is currently in development and may be unstable.",
            "ko" => "현재 개발 단계이므로 불안정할 수 있습니다.",
            "ja" => "現在開発段階のため、不安定な場合があります。",
            "vi" => "Ứng dụng đang trong giai đoạn phát triển và có thể chưa ổn định.",
            _ => "目前处于开发阶段，可能存在不稳定情况！"
        };
        TestTranslationButton.Content = language switch
        {
            "zh-Hant" => "測試連線/測試翻譯",
            "en" => "Test Connection / Translation",
            "ko" => "연결/번역 테스트",
            "ja" => "接続/翻訳テスト",
            "vi" => "Kiểm tra kết nối / dịch",
            _ => "测试连接/测试翻译"
        };
        MyMemoryTranslatorNoticeTextBlock.Text = language switch
        {
            "zh-Hant" => "MyMemory 不需要 API Key，支援簡體中文、繁體中文、English、한국어、日本語、Tiếng Việt 之間互譯；來源語言為自動偵測時會按文字特徵做輕量判斷。",
            "en" => "MyMemory does not need an API key. It supports translation among 简体中文, 繁體中文, English, 한국어, 日本語, and Tiếng Việt; auto-detect uses lightweight text features.",
            "ko" => "MyMemory는 API Key가 필요 없습니다. 简体中文, 繁體中文, English, 한국어, 日本語, Tiếng Việt 간 번역을 지원하며 자동 감지는 텍스트 특징으로 가볍게 판단합니다.",
            "ja" => "MyMemory は API Key 不要です。简体中文、繁體中文、English、한국어、日本語、Tiếng Việt の相互翻訳に対応し、自動検出では文字特徴から軽く判定します。",
            "vi" => "MyMemory không cần API Key. Hỗ trợ dịch giữa 简体中文, 繁體中文, English, 한국어, 日本語 và Tiếng Việt; tự động nhận dạng dựa trên đặc trưng văn bản.",
            _ => "MyMemory 不需要 API Key，支持简体中文、繁體中文、English、한국어、日本語、Tiếng Việt 之间互译；源语言为自动检测时会按文本特征做轻量判断。"
        };
        LocalizeLanguageComboBox(UiLanguageComboBox, language, useNativeNames: true);
        LocalizeLanguageComboBox(SourceLanguageComboBox, language, useNativeNames: false);
        LocalizeLanguageComboBox(TargetLanguageComboBox, language, useNativeNames: false);
        LocalizeLanguageComboBox(OverlayInputTargetLanguageComboBox, language, useNativeNames: false);
        LocalizeLanguageComboBox(OverlayInputDefaultTargetLanguageComboBox, language, useNativeNames: false);
        RefreshLocalizedComboBoxSelections();
        RefreshRegionSummary();
        UpdateExcludedPlayersLimitState();
        UpdateTranslatorSettingsVisibility();
        ApplySettingHelpTooltips();
    }

    private void RefreshLocalizedComboBoxSelections()
    {
        var selectedUiLanguage = LocalizationService.NormalizeLanguage(GetSelectedTag(UiLanguageComboBox, _config.UiLanguage));
        var selectedSourceLanguage = GetSelectedTag(SourceLanguageComboBox, _config.TranslateConfig.SourceLanguage);
        var selectedTargetLanguage = GetSelectedTag(TargetLanguageComboBox, TranslatorLanguage.NormalizeTargetLanguage(_config.TranslateConfig.TargetLanguage));
        var selectedOverlayInputTargetLanguage = GetSelectedTag(OverlayInputTargetLanguageComboBox, NormalizeOverlayInputTargetLanguage(_config.TranslateConfig.OverlayInputTargetLanguage));
        var selectedOverlayInputDefaultTargetLanguage = GetSelectedTag(OverlayInputDefaultTargetLanguageComboBox, TranslatorLanguage.NormalizeTargetLanguage(_config.TranslateConfig.OverlayInputDefaultTargetLanguage));
        var selectedToxicDisplayMode = GetSelectedTag(ToxicDisplayModeComboBox, _config.TranslateConfig.ToxicDisplayMode);
        var selectedOcrEngine = OcrEngines.Normalize(GetSelectedTag(OcrEngineComboBox, _config.OcrConfig.OcrEngine));
        var selectedOcrLanguage = OcrLanguages.Normalize(GetSelectedTag(OcrLanguageComboBox, _config.OcrConfig.OcrLanguage));
        var selectedAdvancedOcrLanguage = OcrLanguages.Normalize(GetSelectedTag(AdvancedOcrLanguageComboBox, selectedOcrLanguage));
        var selectedOcrMode = OcrMode.Normalize(GetSelectedTag(OcrModeComboBox, _config.OcrConfig.OcrMode));

        var wasLoadingControls = _isLoadingControls;
        _isLoadingControls = true;
        try
        {
            RefreshComboBoxSelectionByTag(UiLanguageComboBox, selectedUiLanguage);
            RefreshComboBoxSelectionByTag(SourceLanguageComboBox, selectedSourceLanguage);
            RefreshComboBoxSelectionByTag(TargetLanguageComboBox, selectedTargetLanguage);
            RefreshComboBoxSelectionByTag(OverlayInputTargetLanguageComboBox, selectedOverlayInputTargetLanguage);
            RefreshComboBoxSelectionByTag(OverlayInputDefaultTargetLanguageComboBox, selectedOverlayInputDefaultTargetLanguage);
            RefreshComboBoxSelectionByTag(ToxicDisplayModeComboBox, selectedToxicDisplayMode);
            RefreshComboBoxSelectionByTag(OcrEngineComboBox, selectedOcrEngine);
            RefreshComboBoxSelectionByTag(OcrLanguageComboBox, selectedOcrLanguage);
            RefreshComboBoxSelectionByTag(AdvancedOcrLanguageComboBox, selectedAdvancedOcrLanguage);
            RefreshComboBoxSelectionByTag(OcrModeComboBox, selectedOcrMode);
        }
        finally
        {
            _isLoadingControls = wasLoadingControls;
        }
    }

    private void ApplySettingHelpTooltips()
    {
        SetHelp(AutoOcrSpeedComboBox, "稳定更保守，均衡适合多数电脑，快速优先响应速度；都会使用完整用户框选区域。", "Stable is conservative, Balanced fits most PCs, Fast prioritizes responsiveness; all use the full user-selected region.");
        SetHelp(DiagnosticModeCheckBox, "开启后才保存更详细的 OCR/worker 日志和调试图片；日常使用建议关闭以减少写盘。", "Enables detailed OCR/worker logs and debug images. Keep it off for daily use to reduce disk writes.");
        SetHelp(CaptureIntervalTextBox, "自动 OCR 循环的固定扫描间隔，单位毫秒。textmask 关闭时会按此间隔稳定扫描。", "Fixed interval for automatic OCR scans, in milliseconds. When text mask detection is off, scans run at this interval.");
        SetHelp(OcrEnvironmentDirectoryTextBox, "PP-OCRv5 的 Python、虚拟环境和 OCR 依赖会安装到这里。默认是程序所在的 LoLChatTranslator 文件夹；已有旧环境不会自动搬迁。", "PP-OCRv5 Python, the virtual environment, and OCR dependencies are installed here. By default this is the LoLChatTranslator app folder; existing environments are not moved automatically.");
        SetHelp(BrowseOcrEnvironmentDirectoryButton, "选择 PP-OCRv5 OCR 环境安装位置。", "Choose where to install the PP-OCRv5 OCR environment.");
        SetHelp(EnableRealtimeLowLatencyModeCheckBox, "保留的低延迟兼容开关；当前自动 OCR 仍由主界面启动/停止控制，且不会改变框选区域。", "Compatibility toggle for low-latency mode. Automatic OCR is still controlled from the main window and this does not change the selected region.");
        SetHelp(EnableAdaptiveDirtyRegionOcrCheckBox, "实验开关，默认关闭。开启后才允许把 text mask 变化行裁成局部图片送入 OCR；半透明聊天框、滚动或长句换行时可能漏识别。", "Experimental and off by default. Only when enabled may text-mask changed lines be cropped into a local OCR image; translucent chat boxes, scrolling, or wrapped long messages can be missed.");
        SetHelp(EnableTextMaskDetectionCheckBox, "只把 text mask 作为是否触发 OCR 的判断和诊断；默认 OCR 输入仍是完整用户框选区域。", "Uses the text mask only as an OCR trigger and diagnostic signal; the default OCR input remains the full user-selected region.");
        SetHelp(EnableFixedBottomOcrCheckBox, "实验开关，默认关闭。开启后才允许固定底部区域策略；普通用户应保持关闭，避免不同聊天框高度漏行。", "Experimental and off by default. Only enables fixed-bottom behavior when explicitly selected; keep it off to avoid missing lines on different chat-box heights.");
        SetHelp(SaveOcrDebugImagesCheckBox, "开启后普通 OCR 会把输入图保存到日志目录；默认关闭以减少写盘和发布包运行时残留。OCR 测试窗口仍会保留自己的预览图。", "When enabled, normal OCR saves its input image to the log directory. This is off by default; the OCR test window still keeps its own preview image.");
        SetHelp(ShowOriginalBeforeTranslationCheckBox, "保留的兼容设置；当前自动 OCR 仍在翻译完成后一次性显示，避免重复输出原文。", "Compatibility setting. Automatic OCR currently displays once after translation to avoid duplicate original-text output.");
        SetHelp(EnableLineDeduplicationCheckBox, "对已处理过的聊天行去重，避免重复翻译。", "Skips chat lines already processed in this run to avoid duplicate translations.");
        SetHelp(OcrEngineComboBox, "Windows OCR：系统自带，免配置；适合作为系统内置 OCR 对照。\n\nPP-OCRv5 多语言版：项目托管 Python 环境，支持多语言模型，适合复杂聊天截图。", "Windows OCR is built in and needs no setup.\n\nPP-OCRv5 Multilingual uses the app-managed Python environment with multilingual model families for chat screenshots.");
        SetHelp(OcrLanguageComboBox, "仅影响 PP-OCRv5 多语言版。自动/中文模型适合简体中文、英文、拼音、繁体中文、日文混合；英文、拉丁语系、韩语等可选择专用识别模型。", "Only affects PP-OCRv5 Multilingual. Auto/Chinese is suitable for mixed Simplified Chinese, English, pinyin, Traditional Chinese, and Japanese; choose dedicated model families for English, Latin, Korean, and other scripts.");
        SetHelp(AdvancedOcrLanguageComboBox, "高级语言模型。普通用户建议使用自动或中文+英文；切错模型会降低识别率。", "Advanced language model. Auto or Chinese+English is recommended; a mismatched model can reduce recognition quality.");
        SetHelp(OcrModeComboBox, "稳定模式优先兼容；快速模式优先实时速度；高精度模式可尝试更慢的模型；实验模式会尝试更多性能参数，不支持的参数会记录并自动忽略。", "Stable prioritizes compatibility; Fast prioritizes realtime speed; Accurate may use slower models; Experimental tries extra performance parameters and records unsupported ones.");
        SetHelp(FullRescanIntervalTextBox, "自动 OCR 的完整区域重扫间隔。即使截图 hash 相同，到期或上轮失败也会允许重试。", "Full-region rescan interval for automatic OCR. Even if the screenshot hash is unchanged, due rescans or failed previous cycles are allowed to retry.");
        SetHelp(MaxDirtyRegionRatioTextBox, "文字 mask 变化面积超过该比例时，认为布局/滚动变化较大，改跑完整 OCR。", "If text-mask change exceeds this ratio, the cycle is treated as a large layout/scroll change and uses full OCR.");
        SetHelp(DirtyRegionPaddingXTextBox, "dirty 行裁剪左右扩边，避免裁掉时间戳、频道、玩家名或字母边缘。", "Horizontal padding for dirty line crops to avoid clipping timestamps, channels, names, or glyph edges.");
        SetHelp(DirtyRegionPaddingYTextBox, "dirty 行裁剪上下扩边，长句换行时还会自动包含相邻行。", "Vertical padding for dirty line crops. Neighboring lines are included for wrapped long messages.");
        SetHelp(DirtyLineBatchSizeTextBox, "仅实验局部 OCR 使用。一轮最多合并识别的 dirty 行数；超过后回到完整框选区域。", "Used only by experimental local OCR. Maximum dirty lines to merge in one cycle; larger changes fall back to the full selected region.");
        SetHelp(ImageScaleComboBox, "OCR 前图片放大倍率；原图速度最快。", "Image scaling before OCR. Original size is fastest.");
        SetHelp(ContrastTextBox, "OCR 前增强对比度，过高可能带来噪点。", "Contrast enhancement before OCR. Too much contrast can add noise.");
        SetHelp(EnableSharpenCheckBox, "轻微锐化文字笔画，可能改善模糊截图。", "Slightly sharpens text strokes, which may help blurry captures.");
        SetHelp(MinConfidenceTextBox, "低于该置信度的 OCR 行会被过滤。", "OCR lines below this confidence are filtered out.");

        SetHelp(UiLanguageComboBox, "设置软件界面语言。", "Sets the application interface language.");
        SetHelp(SourceLanguageComboBox, "源语言可自动检测；不能与目标语言相同。", "Source language can be auto-detected and cannot be the same as the target language.");
        SetHelp(TargetLanguageComboBox, "翻译输出语言，只能从内置语言中选择。", "Output language for OCR translations. Only built-in languages are supported.");
        SetHelp(TranslateEngineComboBox, "选择翻译服务：MyMemory 免费翻译、AI API 或 Ollama 本地模型。AI API 使用 OpenAI-compatible / DeepSeek / Gemini 等兼容接口时填写对应 API Base。", "Choose MyMemory Free, AI API, or a local Ollama model. AI API can use OpenAI-compatible services such as OpenAI, DeepSeek, or Gemini-compatible endpoints by setting API Base.");
        SetHelp(ToxicDisplayModeComboBox, "命中辱骂内容后在本地处理，不发给普通翻译 API。“显示原意”会把 nmsl 展开为对应辱骂含义；“显示原始 OCR 文本”只显示游戏里识别到的原始字符，例如 nmsl。", "Detected abusive content is handled locally and not sent to the normal translation API. Literal meaning expands terms like nmsl to their meaning; original OCR text shows only the recognized source characters.");
        SetHelp(ExcludePlayersEnabledCheckBox, "开启后，OCR 识别到排除列表内玩家发送的消息会直接跳过，不进入去重、词库匹配或翻译。", "When enabled, messages from excluded players are skipped before dedupe, glossary matching, and translation.");
        SetHelp(AddExcludedPlayerButton, "添加指定 Riot ID，最多 50 个。编号可以输入 1234 或 #1234。", "Adds a Riot ID to exclude. Up to 50 players. The tag can be entered as 1234 or #1234.");
        SetHelp(EnableOverlayInputCheckBox, "开启后悬浮窗下方显示输入框。输入内容会按下方输出语言翻译，并自动复制到剪贴板，不写入悬浮窗消息列表。", "Shows an input box in the overlay. Its reply is translated to the selected output language and copied to the clipboard without adding it to overlay messages.");
        SetHelp(OverlayInputTargetLanguageComboBox, "自动时会跟随最近一条 OCR 聊天的源语言；置信度不足时使用下方默认回复语言。也可以手动选择固定输出语言。", "Auto follows the source language of the most recent OCR chat. If confidence is low, it uses the fallback reply language below. You can also choose a fixed output language.");
        SetHelp(OverlayInputDefaultTargetLanguageComboBox, "自动跟随没有可用最近聊天，或源语言置信度不足时，会使用这个默认回复语言。", "Used when auto follow has no recent chat available, or when source-language confidence is too low.");
        SetHelp(ApiBaseTextBox, "OpenAI-compatible 服务地址，例如 DeepSeek 或自部署网关。", "OpenAI-compatible service URL, such as DeepSeek or a self-hosted gateway.");
        SetHelp(ApiKeyPasswordBox, "你的 API Key；不会写入日志，错误信息也会脱敏。", "Your API key. It is not written to logs and errors are sanitized.");
        SetHelp(ModelTextBox, "AI 翻译模型名称。", "AI translation model name.");

        SetHelp(RemoveUsernameCheckBox, "仅影响翻译输入清洗；悬浮窗发言人显示由“悬浮窗显示发言人”单独控制。", "Only affects translation input cleanup. Sender visibility in the overlay is controlled by Show sender name.");
        SetHelp(RemoveChannelTagCheckBox, "控制悬浮窗标题是否显示队伍/所有人等频道标签；不影响玩家名解析。", "Controls whether the overlay title shows channel tags such as team/all chat. It does not affect player-name parsing.");
        SetHelp(FilterSystemMessagesCheckBox, "过滤游戏系统提示。", "Filters game system messages.");
        SetHelp(FilterPingMessagesCheckBox, "过滤 ping 和信号提示。", "Filters ping and signal messages.");
        SetHelp(FilterKillMessagesCheckBox, "过滤击杀播报。", "Filters kill announcements.");
        SetHelp(FilterPurchaseMessagesCheckBox, "过滤购买装备提示。", "Filters item purchase messages.");

        SetHelp(OpacitySlider, "调整悬浮窗透明度。", "Adjusts overlay opacity.");
        SetHelp(FontSizeTextBox, "调整悬浮窗文字大小。", "Adjusts overlay text size.");
        SetHelp(MaxLinesTextBox, "悬浮窗最多显示的聊天行数。", "Maximum number of chat lines shown in the overlay.");
        SetHelp(ShowSenderNameCheckBox, "默认开启，标题显示 Ntide07（暗黑元首）这类发言人信息；关闭后只显示聊天/频道。", "Enabled by default. Overlay titles show sender info such as player name and champion; when disabled only chat/channel is shown.");
        SetHelp(AlwaysOnTopCheckBox, "让悬浮窗保持在其他窗口上方。", "Keeps the overlay above other windows.");
        SetHelp(ClickThroughCheckBox, "开启后鼠标点击会穿透悬浮窗。", "Allows mouse clicks to pass through the overlay.");
        SetHelp(ExcludeFromScreenCaptureCheckBox, "截图 OCR 时尽量排除悬浮窗，避免识别到自己的翻译。", "Tries to exclude the overlay from OCR capture so it does not read its own translations.");
        SetHelp(HideOverlayDuringCaptureCheckBox, "截图前仅在必要时临时隐藏覆盖识别区域的本程序窗口；悬浮窗已被系统排除截图时不会反复隐藏。", "Only hides app windows that cover the OCR region when needed; the overlay is not repeatedly hidden when Windows capture exclusion is active.");
        SetHelp(OverlayBackgroundColorTextBox, "悬浮窗背景颜色。透明度仍由上方滑块控制。", "Overlay background color. Opacity is still controlled by the slider above.");
        SetHelp(OverlayInputBackgroundColorTextBox, "悬浮窗输入框半透明背景颜色。文字和边框会自动使用高对比度样式。", "Semi-transparent overlay input background color. Text and border use automatic high-contrast styling.");
        SetHelp(TeamHeaderColorTextBox, "队伍频道标题颜色。", "Team channel title color.");
        SetHelp(TeamTextColorTextBox, "队伍频道正文颜色。", "Team channel translation text color.");
        SetHelp(AllHeaderColorTextBox, "所有人频道标题颜色。", "All-chat channel title color.");
        SetHelp(AllTextColorTextBox, "所有人频道正文颜色。", "All-chat translation text color.");
        SetHelp(PartyHeaderColorTextBox, "小队频道标题颜色。", "Party channel title color.");
        SetHelp(PartyTextColorTextBox, "小队频道正文颜色。", "Party channel translation text color.");
        SetHelp(UnknownHeaderColorTextBox, "未知频道标题颜色。", "Unknown channel title color.");
        SetHelp(UnknownTextColorTextBox, "未知频道正文颜色。", "Unknown channel translation text color.");
        SetHelp(SystemHeaderColorTextBox, "系统频道标题颜色。", "System channel title color.");
        SetHelp(SystemTextColorTextBox, "系统频道正文颜色。", "System channel translation text color.");

        SetHelp(ManualTranslateHotkeyTextBox, "手动 OCR 一次的快捷键。", "Hotkey for one manual OCR pass.");
        SetHelp(ToggleAutoTranslateHotkeyTextBox, "启动或停止自动 OCR 的快捷键。", "Hotkey to start or stop automatic OCR.");
        SetHelp(TranslateClipboardHotkeyTextBox, "翻译剪贴板文本并复制结果的快捷键。", "Hotkey to translate clipboard text and copy the result.");
        SetHelp(OpenSettingsHotkeyTextBox, "打开设置窗口的快捷键。", "Hotkey to open Settings.");
        SetHelp(ReselectRegionHotkeyTextBox, "重新框选聊天区域的快捷键。", "Hotkey to select the chat region again.");
        SetHelp(PreviewRegionHotkeyTextBox, "查看当前框选范围的快捷键。", "Hotkey to preview the current selected region.");
        SetHelp(ToggleOverlayHotkeyTextBox, "显示或隐藏输出悬浮窗的快捷键。", "Hotkey to show or hide the output overlay.");
        SetHelp(FocusOverlayInputHotkeyTextBox, "显示悬浮窗并把光标放到输入框，便于快速输入回复。", "Shows the overlay and focuses the input box for quick replies.");
    }

    private void SetHelp(FrameworkElement element, string zhHans, string en)
    {
        var language = LocalizationService.NormalizeLanguage(_config.UiLanguage);
        SetHelp(element, language.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase) ? zhHans : en);
    }

    private static void SetHelp(FrameworkElement element, string helpText)
    {
        element.ToolTip = null;

        if (element is CheckBox checkBox)
        {
            AttachCheckBoxHelpButton(checkBox, helpText);
            return;
        }

        if (FindNearestFormRow(element) is { } row)
        {
            AttachFormRowHelpButton(row, helpText);
        }
    }

    private static void AttachCheckBoxHelpButton(CheckBox checkBox, string helpText)
    {
        if (checkBox.Parent is StackPanel { Tag: string tag } existingWrapper
            && tag.Equals("setting-help-row", StringComparison.Ordinal)
            && existingWrapper.Children.OfType<Button>().FirstOrDefault(IsSettingHelpButton) is { } existingButton)
        {
            UpdateHelpButton(existingButton, helpText);
            return;
        }

        if (checkBox.Parent is not Panel parent)
        {
            return;
        }

        var index = parent.Children.IndexOf(checkBox);
        if (index < 0)
        {
            return;
        }

        var margin = checkBox.Margin;
        checkBox.Margin = new Thickness(0);
        parent.Children.RemoveAt(index);

        var wrapper = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = margin,
            Tag = "setting-help-row"
        };
        wrapper.Children.Add(checkBox);
        wrapper.Children.Add(CreateHelpButton(helpText));
        parent.Children.Insert(index, wrapper);
    }

    private static void AttachFormRowHelpButton(DockPanel row, string helpText)
    {
        var existingButton = row.Children.OfType<Button>().FirstOrDefault(IsSettingHelpButton);
        if (existingButton is not null)
        {
            UpdateHelpButton(existingButton, helpText);
            return;
        }

        var label = row.Children.OfType<TextBlock>().FirstOrDefault();
        if (label is null)
        {
            return;
        }

        label.ToolTip = null;
        var labelIndex = row.Children.IndexOf(label);
        row.Children.Insert(labelIndex + 1, CreateHelpButton(helpText));
    }

    private static Button CreateHelpButton(string helpText)
    {
        var button = new Button
        {
            Content = "?",
            Width = 24,
            MinWidth = 24,
            MaxWidth = 24,
            Height = 24,
            MinHeight = 24,
            MaxHeight = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.White,
            Foreground = System.Windows.Media.Brushes.DimGray,
            BorderBrush = System.Windows.Media.Brushes.LightGray,
            BorderThickness = new Thickness(1),
            FontWeight = FontWeights.SemiBold,
            Tag = "setting-help",
            Focusable = false,
            ToolTip = CreateHelpToolTip(helpText)
        };
        ToolTipService.SetInitialShowDelay(button, 150);
        ToolTipService.SetShowDuration(button, 20000);
        button.Click += SettingHelpButton_Click;
        return button;
    }

    private static void UpdateHelpButton(Button button, string helpText)
    {
        button.ToolTip = CreateHelpToolTip(helpText);
        ToolTipService.SetInitialShowDelay(button, 150);
        ToolTipService.SetShowDuration(button, 20000);
    }

    private static ToolTip CreateHelpToolTip(string helpText)
    {
        return new ToolTip
        {
            Placement = PlacementMode.Right,
            Content = new TextBlock
            {
                Width = 360,
                Text = helpText,
                TextWrapping = TextWrapping.Wrap
            }
        };
    }

    private static bool IsSettingHelpButton(Button button)
    {
        return string.Equals(button.Tag?.ToString(), "setting-help", StringComparison.Ordinal);
    }

    private static void SettingHelpButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ToolTip: ToolTip toolTip } button)
        {
            toolTip.PlacementTarget = button;
            toolTip.Placement = PlacementMode.Right;
            toolTip.IsOpen = true;
            e.Handled = true;
        }
    }

    private static DockPanel? FindNearestFormRow(FrameworkElement element)
    {
        var current = element.Parent as FrameworkElement;
        while (current is not null)
        {
            if (current is DockPanel dockPanel)
            {
                return dockPanel;
            }

            current = current.Parent as FrameworkElement;
        }

        return null;
    }

    private void ReselectRegionButton_Click(object sender, RoutedEventArgs e)
    {
        RegionSelectorWindow selector;
        try
        {
            selector = new RegionSelectorWindow(_config.UiLanguage)
            {
                Owner = this
            };
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "框选窗口打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool? dialogResult;
        try
        {
            dialogResult = selector.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "框选窗口显示失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (dialogResult == true && selector.SelectedRegion is { } region)
        {
            _config.OcrConfig.RegionX = region.X;
            _config.OcrConfig.RegionY = region.Y;
            _config.OcrConfig.RegionWidth = region.Width;
            _config.OcrConfig.RegionHeight = region.Height;
            RefreshRegionSummary();

            try
            {
                OcrCaptureDebugService.SaveDebugImages(_config.OcrConfig, selector.SelectionDebugInfo);
            }
            catch
            {
                // Debug image failures should not block selecting a chat region.
            }
        }
    }

    private void ViewCurrentRegionButton_Click(object sender, RoutedEventArgs e)
    {
        var region = new Int32Rect(
            _config.OcrConfig.RegionX,
            _config.OcrConfig.RegionY,
            Math.Max(1, _config.OcrConfig.RegionWidth),
            Math.Max(1, _config.OcrConfig.RegionHeight));

        var preview = new RegionPreviewWindow(region, _config.UiLanguage)
        {
            Owner = this
        };

        preview.ShowDialog();
    }

    private async void InstallOcrDependenciesButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateConfigFromControls();
        _configService.Save(_config);
        ConfigSaved?.Invoke(this, EventArgs.Empty);
        InstallOcrDependenciesButton.IsEnabled = false;
        BeginOcrInstallProgress();
        OcrDependencyStatusTextBlock.Foreground = System.Windows.Media.Brushes.DimGray;
        OcrDependencyStatusTextBlock.Text = UiTextLocalizer.Text(
            _config.UiLanguage,
            "准备检测 PP-OCRv5 OCR 环境与依赖...",
            "準備偵測 PP-OCRv5 OCR 環境與依賴...",
            "Preparing to check PP-OCRv5 OCR environment...",
            "PP-OCRv5 OCR 환경 확인 준비 중...",
            "PP-OCRv5 OCR 環境確認の準備中...",
            "Đang chuẩn bị kiểm tra môi trường OCR PP-OCRv5...");

        try
        {
            var progress = new Progress<string>(message =>
            {
                OcrDependencyStatusTextBlock.Text = message;
            });
            var detailedProgress = new Progress<OcrDependencyInstallProgress>(AppendOcrInstallProgress);

            var result = await _ocrDependencyInstaller.InstallAllAsync(_config.OcrConfig, progress, detailedProgress: detailedProgress);
            if (!result.Succeeded)
            {
                result = await RetryOcrDependencyInstallAfterFailureAsync(result, progress, detailedProgress);
            }

            OcrDependencyStatusTextBlock.Foreground = result.Succeeded
                ? System.Windows.Media.Brushes.SeaGreen
                : System.Windows.Media.Brushes.Firebrick;
            OcrDependencyStatusTextBlock.Text = result.Message;
            MarkOcrDependenciesInstalledIfSucceeded(result);
        }
        finally
        {
            EndOcrInstallProgress();
            InstallOcrDependenciesButton.IsEnabled = true;
        }
    }

    private async Task<OcrDependencyInstallResult> RetryOcrDependencyInstallAfterFailureAsync(
        OcrDependencyInstallResult result,
        IProgress<string> progress,
        IProgress<OcrDependencyInstallProgress> detailedProgress)
    {
        var currentDirectory = PythonEnvironmentService.ResolveOcrEnvironmentDirectory(_config.OcrConfig);
        var selectedDirectory = OcrEnvironmentInstallRecovery.PromptForAlternativeDirectory(
            this,
            _config.UiLanguage,
            currentDirectory,
            result.Message);

        if (!string.IsNullOrWhiteSpace(selectedDirectory))
        {
            OcrEnvironmentDirectoryTextBox.Text = selectedDirectory;
            _config.OcrConfig.OcrEnvironmentDirectory = selectedDirectory;
            _config.HasCompletedOcrEnvironmentSetup = false;
            _configService.Save(_config);
            ConfigSaved?.Invoke(this, EventArgs.Empty);
            OcrDependencyStatusTextBlock.Foreground = System.Windows.Media.Brushes.DimGray;
            OcrDependencyStatusTextBlock.Text = UiTextLocalizer.Text(
                _config.UiLanguage,
                "正在使用新的 OCR 环境位置重试检测/安装...",
                "正在使用新的 OCR 環境位置重試偵測/安裝...",
                "Retrying OCR environment check/install with the new location...",
                "새 OCR 환경 위치로 확인/설치를 다시 시도하는 중...",
                "新しい OCR 環境の場所で確認/インストールを再試行しています...",
                "Đang thử kiểm tra/cài lại với vị trí OCR mới...");
            AppendOcrInstallConsoleLine($"[{DateTime.Now:HH:mm:ss}] Retrying with OCR environment directory: {selectedDirectory}");

            result = await _ocrDependencyInstaller.InstallAllAsync(_config.OcrConfig, progress, detailedProgress: detailedProgress);
        }

        if (!result.Succeeded && OcrEnvironmentInstallRecovery.ShouldOfferAdministratorRestart(result))
        {
            OcrEnvironmentInstallRecovery.PromptRestartAsAdministrator(this, _config.UiLanguage, result.Message);
        }

        return result;
    }

    private void MarkOcrDependenciesInstalledIfSucceeded(OcrDependencyInstallResult result)
    {
        if (!result.Succeeded)
        {
            return;
        }

        _config.HasCompletedOcrEnvironmentSetup = true;
        _configService.Save(_config);
        ConfigSaved?.Invoke(this, EventArgs.Empty);
        OcrDependenciesInstalled?.Invoke(this, EventArgs.Empty);
    }

    private void BeginOcrInstallProgress()
    {
        OcrInstallProgressBar.Value = 0;
        OcrInstallConsoleTextBox.Clear();
        OcrInstallProgressPanel.Visibility = Visibility.Visible;
        AppendOcrInstallConsoleLine($"[{DateTime.Now:HH:mm:ss}] OCR dependency install started");
    }

    private void EndOcrInstallProgress()
    {
        OcrInstallProgressPanel.Visibility = Visibility.Collapsed;
    }

    private void AppendOcrInstallProgress(OcrDependencyInstallProgress progress)
    {
        if (progress.Percent >= 0)
        {
            OcrInstallProgressBar.Value = Math.Clamp(progress.Percent, 0, 100);
        }

        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            AppendOcrInstallConsoleLine($"[{DateTime.Now:HH:mm:ss}] {progress.Message.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(progress.Detail))
        {
            AppendOcrInstallConsoleLine(progress.Detail.Trim());
        }
    }

    private void AppendOcrInstallConsoleLine(string line)
    {
        OcrInstallConsoleTextBox.AppendText(line + Environment.NewLine);
        OcrInstallConsoleTextBox.ScrollToEnd();
    }

    private async void DeleteOcrEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateConfigFromControls();
        var venvDirectory = PythonEnvironmentService.ResolveOcrVenvDirectory(_config.OcrConfig);
        var confirm = MessageBox.Show(
            this,
            UiTextLocalizer.Text(
                _config.UiLanguage,
                "将删除程序托管的 PP-OCRv5 OCR 虚拟环境：",
                "將刪除程式託管的 PP-OCRv5 OCR 虛擬環境：",
                "This will delete the app-managed PP-OCRv5 OCR virtual environment:",
                "프로그램이 관리하는 PP-OCRv5 OCR 가상 환경을 삭제합니다:",
                "アプリ管理の PP-OCRv5 OCR 仮想環境を削除します:",
                "Sẽ xóa môi trường OCR ảo PP-OCRv5 do ứng dụng quản lý:") + "\n\n" +
            $"{venvDirectory}\n\n" +
            UiTextLocalizer.Text(
                _config.UiLanguage,
                "不会删除用户自己安装的 Python，也不会删除系统 Python。确定继续吗？",
                "不會刪除使用者自行安裝的 Python，也不會刪除系統 Python。確定繼續嗎？",
                "User-installed Python and system Python will not be deleted. Continue?",
                "사용자가 직접 설치한 Python이나 시스템 Python은 삭제하지 않습니다. 계속할까요?",
                "ユーザーが自分でインストールした Python やシステム Python は削除しません。続行しますか？",
                "Python do người dùng tự cài và Python hệ thống sẽ không bị xóa. Tiếp tục?"),
            UiTextLocalizer.Text(_config.UiLanguage, "确认删除 PP-OCRv5 OCR 环境", "確認刪除 PP-OCRv5 OCR 環境", "Confirm PP-OCRv5 OCR Environment Deletion", "PP-OCRv5 OCR 환경 삭제 확인", "PP-OCRv5 OCR環境削除の確認", "Xác nhận xóa môi trường OCR PP-OCRv5"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        DeleteOcrEnvironmentButton.IsEnabled = false;
        DeleteOcrEnvironmentStatusTextBlock.Foreground = System.Windows.Media.Brushes.DimGray;
        DeleteOcrEnvironmentStatusTextBlock.Text = UiTextLocalizer.Text(
            _config.UiLanguage,
            "正在删除 PP-OCRv5 OCR 环境...",
            "正在刪除 PP-OCRv5 OCR 環境...",
            "Deleting PP-OCRv5 OCR environment...",
            "PP-OCRv5 OCR 환경 삭제 중...",
            "PP-OCRv5 OCR 環境を削除中...",
            "Đang xóa môi trường OCR PP-OCRv5...");

        try
        {
            var progress = new Progress<string>(message =>
            {
                DeleteOcrEnvironmentStatusTextBlock.Text = message;
            });

            var result = await _ocrDependencyInstaller.DeleteManagedEnvironmentAsync(_config.OcrConfig, progress);
            DeleteOcrEnvironmentStatusTextBlock.Foreground = result.Succeeded
                ? System.Windows.Media.Brushes.SeaGreen
                : System.Windows.Media.Brushes.Firebrick;
            DeleteOcrEnvironmentStatusTextBlock.Text = result.Message;
        }
        finally
        {
            DeleteOcrEnvironmentButton.IsEnabled = true;
        }
    }

    private void OpenChannelAliasesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = ChannelAliasService.EnsureAliasFileExists();
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                UiTextLocalizer.Text(_config.UiLanguage, "打开频道别名文件失败", "開啟頻道別名檔案失敗", "Failed to Open Channel Alias File", "채널 별칭 파일 열기 실패", "チャンネル別名ファイルを開けません", "Không mở được tệp bí danh kênh"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void SetManualTranslateHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureHotkeyInto(ManualTranslateHotkeyTextBox);
    }

    private void ClearHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string targetTextBoxName }
            && FindName(targetTextBoxName) is TextBox targetTextBox)
        {
            targetTextBox.Clear();
        }
    }

    private void SetToggleAutoTranslateHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureHotkeyInto(ToggleAutoTranslateHotkeyTextBox);
    }

    private void SetTranslateClipboardHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureHotkeyInto(TranslateClipboardHotkeyTextBox);
    }

    private void SetOpenSettingsHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureHotkeyInto(OpenSettingsHotkeyTextBox);
    }

    private void SetReselectRegionHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureHotkeyInto(ReselectRegionHotkeyTextBox);
    }

    private void SetPreviewRegionHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureHotkeyInto(PreviewRegionHotkeyTextBox);
    }

    private void SetToggleOverlayHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureHotkeyInto(ToggleOverlayHotkeyTextBox);
    }

    private void SetFocusOverlayInputHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureHotkeyInto(FocusOverlayInputHotkeyTextBox);
    }

    private void CaptureHotkeyInto(TextBox targetTextBox)
    {
        var captureWindow = new HotkeyCaptureWindow(_config.UiLanguage)
        {
            Owner = this
        };

        if (captureWindow.ShowDialog() == true
            && !string.IsNullOrWhiteSpace(captureWindow.CapturedHotkey))
        {
            targetTextBox.Text = captureWindow.CapturedHotkey;
        }
    }

    private void RefreshRegionSummary()
    {
        var ocr = _config.OcrConfig;
        var widthLabel = UiTextLocalizer.Text(_config.UiLanguage, "宽", "寬", "W", "너비", "幅", "Rộng");
        var heightLabel = UiTextLocalizer.Text(_config.UiLanguage, "高", "高", "H", "높이", "高さ", "Cao");
        RegionSummaryTextBlock.Text = $"X={ocr.RegionX}, Y={ocr.RegionY}, {widthLabel}={ocr.RegionWidth}, {heightLabel}={ocr.RegionHeight}";
    }

    private static AppConfig CloneConfig(AppConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? AppConfig.CreateDefault();
        }
        catch
        {
            return AppConfig.CreateDefault();
        }
    }

    private static int ReadInt(TextBox textBox, int fallback, int? min = null, int? max = null)
    {
        if (!int.TryParse(textBox.Text.Trim(), out var value))
        {
            return fallback;
        }

        if (min.HasValue)
        {
            value = Math.Max(min.Value, value);
        }

        if (max.HasValue)
        {
            value = Math.Min(max.Value, value);
        }

        return value;
    }

    private static double ReadDouble(TextBox textBox, double fallback, double min, double max)
    {
        if (!double.TryParse(textBox.Text.Trim(), out var value))
        {
            return fallback;
        }

        return Clamp(value, min, max);
    }

    private static double ReadDoubleFromComboBox(ComboBox comboBox, double fallback, double min, double max)
    {
        var raw = (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? comboBox.Text;
        if (!double.TryParse(raw, out var value))
        {
            return fallback;
        }

        return Clamp(value, min, max);
    }

    private static string ReadColor(TextBox textBox, string fallback)
    {
        var value = textBox.Text.Trim();
        return IsHexColor(value) ? value : fallback;
    }

    private static bool IsHexColor(string value)
    {
        if (value.Length is not (7 or 9) || value[0] != '#')
        {
            return false;
        }

        return value.Skip(1).All(Uri.IsHexDigit);
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Min(max, Math.Max(min, value));
    }

    private static void SelectComboBoxByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = comboBox.Items.Count > 0 ? 0 : -1;
    }

    private static void RefreshComboBoxSelectionByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (!string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ReferenceEquals(comboBox.SelectedItem, item))
            {
                comboBox.SelectedItem = item;
                return;
            }

            var selectedIndex = comboBox.SelectedIndex;
            comboBox.SelectedIndex = -1;
            comboBox.SelectedIndex = selectedIndex;
            return;
        }

        comboBox.SelectedIndex = comboBox.Items.Count > 0 ? 0 : -1;
    }

    private static void LocalizeLanguageComboBox(ComboBox comboBox, string uiLanguage, bool useNativeNames)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag?.ToString() is { } languageCode)
            {
                item.Content = languageCode.Equals("ocr-reverse", StringComparison.OrdinalIgnoreCase)
                    ? LocalizationService.T(uiLanguage, "AutoFollowRecentChat")
                    : useNativeNames
                    ? LocalizationService.LanguageName(uiLanguage, languageCode)
                    : LocalizationService.LocalizedLanguageName(uiLanguage, languageCode);
            }
        }
    }

    private static string NormalizeOverlayInputTargetLanguage(string? language)
    {
        return string.Equals(language, "ocr-reverse", StringComparison.OrdinalIgnoreCase)
            ? "ocr-reverse"
            : TranslatorLanguage.NormalizeTargetLanguage(language);
    }

    private void LocalizeToxicDisplayModeComboBox(string uiLanguage)
    {
        foreach (var item in ToxicDisplayModeComboBox.Items.OfType<ComboBoxItem>())
        {
            item.Content = item.Tag?.ToString() switch
            {
                "hide" => uiLanguage switch
                {
                    "zh-Hant" => "僅顯示辱罵提示",
                    "en" => "Generic abuse warning only",
                    "ko" => "일반 욕설 경고만 표시",
                    "ja" => "一般的な暴言警告のみ表示",
                    "vi" => "Chỉ hiện cảnh báo chung",
                    _ => "仅显示辱骂提示"
                },
                "literal" => uiLanguage switch
                {
                    "zh-Hant" => "顯示原意，可能顯示辱罵內容",
                    "en" => "Show literal meaning, may show abuse",
                    "ko" => "원뜻 표시, 욕설 내용이 보일 수 있음",
                    "ja" => "原意を表示、暴言内容が出る場合あり",
                    "vi" => "Hiện nghĩa gốc, có thể hiện lời xúc phạm",
                    _ => "显示原意，可能显示辱骂内容"
                },
                "source" => uiLanguage switch
                {
                    "zh-Hant" => "顯示原始 OCR 文字",
                    "en" => "Show original OCR text",
                    "ko" => "원본 OCR 텍스트 표시",
                    "ja" => "元の OCR テキストを表示",
                    "vi" => "Hiện văn bản OCR gốc",
                    _ => "显示原始 OCR 文本"
                },
                _ => uiLanguage switch
                {
                    "zh-Hant" => "安全標籤，推薦",
                    "en" => "Safe label, recommended",
                    "ko" => "안전 라벨, 권장",
                    "ja" => "安全ラベル、推奨",
                    "vi" => "Nhãn an toàn, khuyến nghị",
                    _ => "安全标签，推荐"
                }
            };
        }
    }

    private static string GetSelectedTag(ComboBox comboBox, string fallback)
    {
        return (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
    }

    private static bool IsKnownDefaultApiBase(string value)
    {
        return value.Equals(TranslatorEngines.OpenAICompatibleDefaultApiBase, StringComparison.OrdinalIgnoreCase)
            || value.Equals(TranslatorEngines.DeepSeekDefaultApiBase, StringComparison.OrdinalIgnoreCase)
            || value.Equals(TranslatorEngines.GeminiDefaultApiBase, StringComparison.OrdinalIgnoreCase)
            || value.Equals(TranslatorEngines.OllamaDefaultApiBase, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownDefaultModel(string value)
    {
        return value.Equals(TranslatorEngines.OpenAICompatibleDefaultModel, StringComparison.OrdinalIgnoreCase)
            || value.Equals(TranslatorEngines.DeepSeekDefaultModel, StringComparison.OrdinalIgnoreCase)
            || value.Equals(TranslatorEngines.GeminiDefaultModel, StringComparison.OrdinalIgnoreCase)
            || value.Equals(TranslatorEngines.OllamaDefaultModel, StringComparison.OrdinalIgnoreCase);
    }

    private static Version GetCurrentVersion()
    {
        return typeof(SettingsWindow).Assembly.GetName().Version ?? new Version(1, 0, 0);
    }

    private static string GetCurrentVersionText()
    {
        var informationalVersion = typeof(SettingsWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+')[0];
        }

        return FormatVersion(GetCurrentVersion());
    }

    private static string FormatVersion(Version version)
    {
        return version.Build >= 0
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : $"{version.Major}.{version.Minor}";
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                string.Format(
                    UiTextLocalizer.Text(_config.UiLanguage, "打开链接失败：{0}", "開啟連結失敗：{0}", "Failed to open link: {0}", "링크 열기 실패: {0}", "リンクを開けません: {0}", "Không mở được liên kết: {0}"),
                    ex.Message),
                UiTextLocalizer.Text(_config.UiLanguage, "打开链接失败", "開啟連結失敗", "Failed to Open Link", "링크 열기 실패", "リンクを開けません", "Không mở được liên kết"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
