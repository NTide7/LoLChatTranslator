using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using LoLChatTranslator.Models;
using LoLChatTranslator.Services;
using Bitmap = System.Drawing.Bitmap;

namespace LoLChatTranslator;

public partial class MainWindow : Window
{
    private const int AutoOcrMinimumTimeoutMs = 8000;
    private const int AutoOcrColdStartTimeoutMs = 30000;
    private const int CaptureHideDelayMs = 60;
    private const double PreferredStartupWidth = 1040;
    private const double PreferredStartupHeight = 760;
    private const double MinimumStartupWidth = 760;
    private const double MinimumStartupHeight = 520;
    private static readonly TimeSpan SelfOcrIgnoreTtl = TimeSpan.FromSeconds(25);

    private readonly ConfigService _configService = new();
    private readonly ChampionAliasService _championAliasService = new();
    private readonly OcrService _ocrService = new();
    private readonly OcrDependencyInstallerService _ocrDependencyInstaller = new();
    private readonly ChannelAliasService _channelAliasService;
    private readonly ChatCleaner _chatCleaner;
    private readonly ChatDeduper _chatDeduper;
    private readonly MessageNormalizer _messageNormalizer = new();
    private readonly PlayerNameMatcher _playerNameMatcher = new();
    private readonly HotkeyService _hotkeyService = new();
    private readonly OcrTextMaskTrigger _ocrTextMaskTrigger = new();
    private readonly PendingMessageStabilizer _pendingMessageStabilizer = new();
    private readonly AutoOcrCoordinator _autoOcrCoordinator = new();
    private readonly Queue<OcrTimingSample> _recentOcrTimings = new();
    private readonly Queue<string> _realtimeDebugLines = new();
    private readonly Queue<RecentOwnOutputText> _recentOwnOutputTexts = new();
    private readonly RealtimeOcrDebugState _realtimeOcrDebug = new();
    private readonly Dictionary<string, PendingOutputItem> _pendingOutputItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _diagnosticSessionId = Guid.NewGuid().ToString("N");

    private readonly LiveClientDataService _liveClientDataService;
    private AppConfig _config;
    private TranslateService _translateService;
    private OverlayWindow _overlayWindow;
    private SettingsWindow? _settingsWindow;
    private LastTranslationEvent? _lastTranslationEvent;
    private string? _selfPlayerName;
    private readonly bool _installOcrDependenciesOnStartup;
    private Task? _autoTranslateTask;
    private bool _isAutoTranslating;
    private bool _closeRequested;
    private bool _closeReady;
    private bool _closeCleanupCompleted;
    private bool _isOcrDependencyInstallRunning;
    private DateTimeOffset _lastAutoOcrRunAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAutoFullOcrRunAt = DateTimeOffset.MinValue;
    private int _autoOcrInFlight;
    private int _autoOcrGeneration;
    private int _ocrWarmUpInFlight;
    private DateTimeOffset _autoOcrInFlightStartedAt = DateTimeOffset.MinValue;
    private string? _lastAutoCaptureHash;
    private string _lastOcrCycleCommitReason = "failed_not_committed";

    public MainWindow(bool installOcrDependenciesOnStartup = false)
    {
        InitializeComponent();
        ConfigureInitialWindowSize();

        _installOcrDependenciesOnStartup = installOcrDependenciesOnStartup;
        _channelAliasService = new ChannelAliasService();
        _chatCleaner = new ChatCleaner(_channelAliasService);
        _chatDeduper = new ChatDeduper(_channelAliasService);
        _config = _configService.Load();
        AppLogService.EnableVerboseDiagnostics = _config.EnableVerboseDiagnostics;
        _liveClientDataService = new LiveClientDataService(_championAliasService);
        _translateService = new TranslateService(_config);
        _overlayWindow = new OverlayWindow();
        _overlayWindow.InputSubmitted += TranslateOverlayInputAsync;

        Loaded += MainWindow_Loaded;
    }

    private void ConfigureInitialWindowSize()
    {
        var workArea = SystemParameters.WorkArea;
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            return;
        }

        var maxStartupWidth = Math.Max(320, workArea.Width * 0.92);
        var maxStartupHeight = Math.Max(320, workArea.Height * 0.90);
        MinWidth = Math.Min(MinWidth, Math.Min(MinimumStartupWidth, maxStartupWidth));
        MinHeight = Math.Min(MinHeight, Math.Min(MinimumStartupHeight, maxStartupHeight));
        Width = Math.Clamp(PreferredStartupWidth, MinWidth, maxStartupWidth);
        Height = Math.Clamp(PreferredStartupHeight, MinHeight, maxStartupHeight);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyConfigToUi();
        _overlayWindow.ApplyConfig(_config);
        _overlayWindow.Show();
        UpdateOverlayVisibilityButton();
        UpdateOverlayInputTargetStatus();
        RegisterHotkeys();
        UpdateAutoButtons();
        UpdateOcrEnvironmentSetupButtonVisibility();
        UpdateRealtimeOcrDebug(state => state.RunningStatus = "stopped");
        SetStatus(T("StatusReady"));
        DiagnosticSnapshotService.WriteStartupSnapshot(_config, _diagnosticSessionId);
        ScheduleOcrWarmUp("startup", delayMs: 2000);
        ShowDevelopmentNoticeOnce();
        if (_installOcrDependenciesOnStartup)
        {
            _config.HasShownOcrEnvironmentSetupPrompt = true;
            _configService.Save(_config);
            TryBeginInvoke(async () => await InstallOcrDependenciesFromMainAsync("elevated_startup"));
        }
        else
        {
            ShowOcrEnvironmentSetupPromptOnce();
        }
    }

    private void StartAutoButton_Click(object sender, RoutedEventArgs e)
    {
        StartAutoTranslate();
    }

    private async void StopAutoButton_Click(object sender, RoutedEventArgs e)
    {
        await StopAutoTranslateAsync("stop_button");
    }

    private async void ManualRecognizeButton_Click(object sender, RoutedEventArgs e)
    {
        await RunManualTranslateOnceAsync();
    }

    private async void RestartTranslateButton_Click(object sender, RoutedEventArgs e)
    {
        await RestartTranslateAsync();
    }

    private async void InstallOcrDependenciesMainButton_Click(object sender, RoutedEventArgs e)
    {
        await InstallOcrDependenciesFromMainAsync("main_button");
    }

    private async void TestOcrButton_Click(object sender, RoutedEventArgs e)
    {
        await TestOcrAsync();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsWindow();
    }

    private void ReselectRegionButton_Click(object sender, RoutedEventArgs e)
    {
        ReselectRegion();
    }

    private void ViewCurrentRegionButton_Click(object sender, RoutedEventArgs e)
    {
        PreviewCurrentRegion();
    }

    private void ToggleOverlayVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleOverlayVisibility();
    }

    private async void TranslateReplyButton_Click(object sender, RoutedEventArgs e)
    {
        await TranslateReplyInputAndCopyAsync();
    }

    private void StartAutoTranslate()
    {
        if (_isAutoTranslating || _autoOcrCoordinator.IsRunning)
        {
            return;
        }

        _isAutoTranslating = true;
        UpdateAutoButtons();
        _lastAutoOcrRunAt = DateTimeOffset.MinValue;
        _lastAutoFullOcrRunAt = DateTimeOffset.MinValue;
        _lastAutoCaptureHash = null;
        _ocrTextMaskTrigger.Reset();
        var shouldResetOcrWorker = Volatile.Read(ref _autoOcrInFlight) != 0
            || Volatile.Read(ref _ocrWarmUpInFlight) != 0;
        ResetAutoOcrWorkerState("start_auto", resetWorkers: shouldResetOcrWorker);
        UpdateRealtimeOcrDebug(state =>
        {
            state.RunningStatus = "running";
            state.LastSkipReason = "-";
            state.LastError = "-";
        });
        AddRealtimeDebugLog("[AutoOcr][Start] auto translate enabled");
        AddRealtimeDebugLog("tick: worker started");
        SetStatus(T("StatusAutoStarted"));

        AutoOcrSession session;
        try
        {
            session = _autoOcrCoordinator.Start(AutoTranslateLoopAsync);
        }
        catch (Exception ex)
        {
            _isAutoTranslating = false;
            UpdateAutoButtons();
            AddRealtimeDebugLog($"auto_start_failed error=\"{CleanLog(ex.Message)}\"");
            SetStatus(FormatT("StatusAutoStoppedWithError", ex.Message));
            return;
        }

        _autoTranslateTask = session.Task;
        _ = _autoTranslateTask.ContinueWith(task =>
        {
            if (!_autoOcrCoordinator.IsCurrent(session.Generation))
            {
                return;
            }

            if (!task.IsFaulted)
            {
                TryBeginInvoke(() =>
                {
                    _isAutoTranslating = false;
                    UpdateAutoButtons();
                    UpdateRealtimeOcrDebug(state => state.RunningStatus = "stopped");
                });
                return;
            }

            var message = task.Exception?.GetBaseException().Message ?? "unknown";
            AddRealtimeDebugLog($"error: {message}");
            UpdateRealtimeOcrDebug(state =>
            {
                state.RunningStatus = "stopped";
                state.LastError = message;
            });
            SetStatus(FormatT("StatusAutoStoppedWithError", message));
            TryBeginInvoke(() =>
            {
                _isAutoTranslating = false;
                UpdateAutoButtons();
            });
        }, TaskScheduler.Default);
    }

    private async Task StopAutoTranslateAsync(string reason = "unknown", TimeSpan? timeout = null)
    {
        if (!_isAutoTranslating && !_autoOcrCoordinator.IsRunning)
        {
            return;
        }

        _isAutoTranslating = false;
        _pendingMessageStabilizer.Clear();
        UpdateAutoButtons();
        UpdateRealtimeOcrDebug(state => state.RunningStatus = "stopped");
        AddRealtimeDebugLog($"skipped: worker stopped reason={reason}");
        SetStatus(T("StatusAutoStopped"));

        var stopResult = await _autoOcrCoordinator.StopAsync(timeout ?? TimeSpan.FromSeconds(8));
        _autoTranslateTask = stopResult.PreviousTask;
        ResetAutoOcrWorkerState($"stop_auto:{reason}", resetWorkers: false);
        if (!stopResult.Completed)
        {
            AddRealtimeDebugLog($"auto_stop_timeout reason={reason}");
        }
    }

    private async void ToggleAutoTranslate()
    {
        if (_isAutoTranslating)
        {
            await StopAutoTranslateAsync("toggle_hotkey");
        }
        else
        {
            StartAutoTranslate();
        }
    }

    private async Task AutoTranslateLoopAsync(long sessionId, CancellationToken cancellationToken)
    {
        var initialScan = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_autoOcrCoordinator.IsCurrent(sessionId))
            {
                break;
            }

            var loop = Stopwatch.StartNew();
            try
            {
                AddRealtimeDebugLog("tick");
                AddRealtimeDebugLog($"[AutoOcr][InitialScan] {initialScan.ToString().ToLowerInvariant()}");

                var capture = Stopwatch.StartNew();
                using var chatFrame = await CaptureChatFrameWithoutOverlayAsync(cancellationToken, bottomOnly: true);
                capture.Stop();

                if (chatFrame is null)
                {
                    SetRealtimeSkip("no_region");
                    AddRealtimeDebugLog("skipped: no_region skipped_reason=no_region");
                    await DelayRealtimeLoopAsync(loop.ElapsedMilliseconds, cancellationToken);
                    continue;
                }

                UpdateRealtimeOcrDebug(state => state.LastCaptureTime = DateTime.Now.ToString("HH:mm:ss"));
                AddRealtimeDebugLog("captured");

                var captureHash = OcrService.ComputeImageFingerprint(chatFrame);
                var fullRescanDue = IsAutoFullRescanDue();
                var hasPendingStabilityMessage = _pendingMessageStabilizer.HasPending;
                var hasRetryableOutput = HasRetryableOutputItems();
                var retryDue = IsLastOcrCycleRetryable() || hasPendingStabilityMessage || hasRetryableOutput;
                if (!initialScan
                    && !fullRescanDue
                    && !retryDue
                    && string.Equals(captureHash, _lastAutoCaptureHash, StringComparison.Ordinal))
                {
                    SetRealtimeSkip("duplicate_screenshot");
                    AddRealtimeDebugLog($"duplicate_screenshot_skip hash={captureHash} full_rescan_due=false retry_due=false");
                    AddRealtimeDebugLog("skipped: duplicate_screenshot skipped_reason=duplicate_screenshot");
                    await DelayRealtimeLoopAsync(loop.ElapsedMilliseconds, cancellationToken);
                    continue;
                }

                if (!initialScan && string.Equals(captureHash, _lastAutoCaptureHash, StringComparison.Ordinal))
                {
                    if (fullRescanDue)
                    {
                        AddRealtimeDebugLog($"duplicate_screenshot_but_full_rescan_due hash={captureHash}");
                    }
                    else if (retryDue)
                    {
                        AddRealtimeDebugLog($"duplicate_screenshot_but_retry_due hash={captureHash} last_commit={_lastOcrCycleCommitReason} pending_stability={hasPendingStabilityMessage.ToString().ToLowerInvariant()} retryable_output={hasRetryableOutput.ToString().ToLowerInvariant()}");
                    }
                }

                var trigger = initialScan
                    ? new RealtimeOcrTriggerFrame(
                        OcrTextMaskTrigger.CropBottom(chatFrame, _config.OcrConfig),
                        "auto-start",
                        0,
                        0,
                        UseFullOcr: true,
                        FullRescanReason: "initial_scan")
                    : hasPendingStabilityMessage
                        ? new RealtimeOcrTriggerFrame(
                            OcrTextMaskTrigger.CropBottom(chatFrame, _config.OcrConfig),
                            "pending-stability",
                            0,
                            0,
                            UseFullOcr: true,
                            FullRescanReason: "pending_partial_confirmation")
                    : ResolveRealtimeOcrTrigger(chatFrame, capture.ElapsedMilliseconds);
                if (trigger.Snapshot is null)
                {
                    SetRealtimeSkip(trigger.Reason);
                    AddRealtimeDebugLog($"[AutoOcr][Skipped] reason={CleanLog(trigger.Reason)}");
                    AddRealtimeDebugLog($"skipped: {trigger.Reason} skipped_reason={CleanLog(trigger.Reason)}");
                    await DelayRealtimeLoopAsync(loop.ElapsedMilliseconds, cancellationToken);
                    continue;
                }

                using (trigger.Snapshot)
                {
                    _lastAutoOcrRunAt = DateTimeOffset.UtcNow;
                    if (trigger.UseFullOcr)
                    {
                        _lastAutoFullOcrRunAt = DateTimeOffset.UtcNow;
                    }

                    UpdateRealtimeOcrDebug(state =>
                    {
                        state.LastTriggerReason = trigger.Reason;
                        state.LastTranslationStatus = "recognizing";
                    });
                    AddRealtimeDebugLog(
                        $"triggered: {trigger.Reason} " +
                        $"dirty_line_count={trigger.DirtyLineCount} used_full_ocr={trigger.UseFullOcr.ToString().ToLowerInvariant()} " +
                        $"full_rescan_reason={CleanLog(trigger.FullRescanReason ?? string.Empty)} " +
                        $"changed_pixels={trigger.ChangedPixels} dirty_ratio={trigger.DirtyRatio:0.###} " +
                        $"crop_regions=\"{CleanLog(trigger.CropRegions ?? string.Empty)}\"");

                    var cycleCompleted = await RunOcrCleanTranslateOnceAsync(
                        cancellationToken,
                        forceFlushPending: initialScan,
                        includePreviouslyTranslated: false,
                        cachedFrame: trigger.Snapshot,
                        source: trigger.Reason,
                        captureMs: capture.ElapsedMilliseconds,
                        cropMs: trigger.CropMs,
                        maskMs: trigger.MaskMs,
                        dirtyLineCount: trigger.DirtyLineCount,
                        fullRescanReason: trigger.FullRescanReason,
                        usedFullOcr: trigger.UseFullOcr,
                        usedRecognitionOnly: false,
                        recognitionOnlyReason: trigger.UseFullOcr ? null : "api_not_supported_local_region_full_ocr_fallback",
                        localRegionFullOcr: !trigger.UseFullOcr,
                        changedPixels: trigger.ChangedPixels,
                        dirtyRegionRatio: trigger.DirtyRatio,
                        cropRegions: trigger.CropRegions,
                        textMaskChanged: trigger.TextMaskChanged);
                    if (!_autoOcrCoordinator.IsCurrent(sessionId))
                    {
                        _ocrTextMaskTrigger.RollbackPendingFrame("stale_session_not_committed");
                        AddRealtimeDebugLog($"session_stale_skip_commit session={sessionId}");
                        break;
                    }

                    if (cycleCompleted)
                    {
                        var commitReason = string.IsNullOrWhiteSpace(_lastOcrCycleCommitReason)
                            ? "ocr_success"
                            : _lastOcrCycleCommitReason;
                        _ocrTextMaskTrigger.CommitPendingFrame(trigger.MaskLineHashes, commitReason);
                        _lastAutoCaptureHash = captureHash;
                        AddRealtimeDebugLog($"hash_committed hash={captureHash} commit_reason={commitReason}");
                        AddRealtimeDebugLog($"text_mask_frame_committed=true commit_reason={commitReason}");
                    }
                    else
                    {
                        _ocrTextMaskTrigger.RollbackPendingFrame("failed_not_committed");
                        AddRealtimeDebugLog("text_mask_frame_committed=false commit_reason=failed_not_committed");
                    }
                }

                initialScan = false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _ocrTextMaskTrigger.RollbackPendingFrame("cancelled_not_committed");
                break;
            }
            catch (Exception ex)
            {
                _ocrTextMaskTrigger.RollbackPendingFrame("exception_not_committed");
                var message = ex.Message;
                AddRealtimeDebugLog($"error: {message}");
                UpdateRealtimeOcrDebug(state =>
                {
                    state.LastError = message;
                    state.LastTranslationStatus = "error";
                });
                SetStatus(FormatT("StatusAutoError", message));
            }

            await DelayRealtimeLoopAsync(loop.ElapsedMilliseconds, cancellationToken);
        }
    }

    private RealtimeOcrTriggerFrame ResolveRealtimeOcrTrigger(Bitmap chatFrame, long captureMs)
    {
        var fallbackDue = IsAutoFullRescanDue();

        if (!_config.OcrConfig.EnableAdaptiveDirtyRegionOcr && !_config.OcrConfig.EnableTextMaskDetection)
        {
            return new RealtimeOcrTriggerFrame(
                OcrTextMaskTrigger.CropBottom(chatFrame, _config.OcrConfig),
                "fixed_interval",
                0,
                0,
                UseFullOcr: true,
                FullRescanReason: _config.OcrConfig.EnableFixedBottomOcr ? "fixed_bottom_experimental" : "fixed_interval");
        }

        var trigger = _ocrTextMaskTrigger.Evaluate(chatFrame, _config.OcrConfig);
        if (trigger.ShouldRunOcr && trigger.OcrSnapshot is not null)
        {
            return new RealtimeOcrTriggerFrame(
                trigger.OcrSnapshot,
                trigger.Reason,
                trigger.CropMs,
                trigger.MaskMs,
                trigger.NewLineHashes,
                trigger.UseFullOcr,
                trigger.FullRescanReason,
                trigger.DirtyLineCount,
                trigger.ChangedPixels,
                trigger.DirtyRatio,
                FormatDirtyRegions(trigger.DirtyRegions),
                trigger.TextMaskChanged);
        }

        trigger.OcrSnapshot?.Dispose();

        if (fallbackDue)
        {
            return new RealtimeOcrTriggerFrame(
                OcrTextMaskTrigger.CropBottom(chatFrame, _config.OcrConfig),
                "full_rescan_interval",
                trigger.CropMs,
                trigger.MaskMs,
                UseFullOcr: true,
                FullRescanReason: "full_rescan_interval",
                ChangedPixels: trigger.ChangedPixels,
                DirtyRatio: trigger.DirtyRatio,
                TextMaskChanged: trigger.TextMaskChanged);
        }

        return new RealtimeOcrTriggerFrame(
            null,
            string.IsNullOrWhiteSpace(trigger.Reason) ? "mask_no_change" : trigger.Reason,
            trigger.CropMs,
            trigger.MaskMs,
            DirtyLineCount: trigger.DirtyLineCount,
            ChangedPixels: trigger.ChangedPixels,
            DirtyRatio: trigger.DirtyRatio,
            CropRegions: FormatDirtyRegions(trigger.DirtyRegions),
            TextMaskChanged: trigger.TextMaskChanged);
    }

    private bool IsAutoFullRescanDue()
    {
        var fallbackIntervalMs = Math.Max(1200, _config.OcrConfig.FullRescanIntervalMs);
        return _lastAutoFullOcrRunAt == DateTimeOffset.MinValue
            || DateTimeOffset.UtcNow - _lastAutoFullOcrRunAt >= TimeSpan.FromMilliseconds(fallbackIntervalMs);
    }

    private bool IsLastOcrCycleRetryable()
    {
        return _lastOcrCycleCommitReason is
            "failed_not_committed" or
            "translation_failed" or
            "untranslated_output" or
            "ocr_timeout" or
            "worker_error" or
            "no_ocr_lines" or
            "no_valid_chat" or
            "pending_unstable";
    }

    private bool HasRetryableOutputItems()
    {
        var now = DateTimeOffset.UtcNow;
        return _pendingOutputItems.Values.Any(item =>
            item.Status == OutputItemStatus.Failed
            && now - item.UpdatedAt >= TimeSpan.FromMilliseconds(Math.Max(750, _config.OcrConfig.CaptureIntervalMs)));
    }

    private async Task DelayRealtimeLoopAsync(long elapsedMs, CancellationToken cancellationToken)
    {
        var intervalMs = Math.Max(250, _config.OcrConfig.CaptureIntervalMs);
        var delayMs = Math.Max(50, intervalMs - (int)Math.Min(elapsedMs, int.MaxValue));
        await Task.Delay(delayMs, cancellationToken);
    }

    private async Task RunManualTranslateOnceAsync()
    {
        ManualRecognizeButton.IsEnabled = false;

        try
        {
            await RunOcrCleanTranslateOnceAsync(
                CancellationToken.None,
                forceFlushPending: true,
                includePreviouslyTranslated: false,
                source: "manual");
        }
        finally
        {
            ManualRecognizeButton.IsEnabled = true;
        }
    }

    private async Task RestartTranslateAsync()
    {
        RestartTranslateButton.IsEnabled = false;
        ManualRecognizeButton.IsEnabled = false;

        try
        {
            await StopAutoTranslateForRestartAsync();

            ClearTranslationState(clearOverlay: true);
            StartAutoTranslate();
        }
        finally
        {
            ManualRecognizeButton.IsEnabled = true;
            RestartTranslateButton.IsEnabled = true;
        }
    }

    private async Task InstallOcrDependenciesFromMainAsync(string source)
    {
        if (_isOcrDependencyInstallRunning)
        {
            return;
        }

        _isOcrDependencyInstallRunning = true;
        InstallOcrDependenciesMainButton.IsEnabled = false;
        InstallOcrDependenciesMainButton.Content = T("OcrEnvironmentInstalling");
        UpdateAutoButtons();
        AddRealtimeDebugLog($"ocr_dependency_install_started source={source}");
        SetStatus(T("OcrEnvironmentPreparing"));

        try
        {
            await StopAutoTranslateAsync("ocr_dependency_install", TimeSpan.FromSeconds(10));
            ResetAutoOcrWorkerState("ocr_dependency_install", resetWorkers: true);

            var progress = new Progress<string>(message =>
            {
                if (!string.IsNullOrWhiteSpace(message))
                {
                    SetStatus(message);
                }
            });
            var detailedProgress = new Progress<OcrDependencyInstallProgress>(progressItem =>
            {
                if (progressItem.Percent >= 0 && !string.IsNullOrWhiteSpace(progressItem.Message))
                {
                    AddRealtimeDebugLog(
                        $"ocr_dependency_install progress={progressItem.Percent} message=\"{CleanLog(progressItem.Message)}\"");
                }

                if (_config.EnableVerboseDiagnostics && !string.IsNullOrWhiteSpace(progressItem.Detail))
                {
                    AddRealtimeDebugLog($"ocr_dependency_install detail=\"{CleanLog(progressItem.Detail)}\"");
                }
            });

            var result = await _ocrDependencyInstaller.InstallAllAsync(
                _config.OcrConfig,
                progress,
                CancellationToken.None,
                detailedProgress);
            if (!result.Succeeded)
            {
                result = await RetryOcrDependencyInstallAfterFailureAsync(result, progress, detailedProgress, source);
            }

            if (result.Succeeded)
            {
                MarkOcrEnvironmentSetupCompleted(source);
                SetStatus(T("OcrEnvironmentReady"));
                AddRealtimeDebugLog($"ocr_dependency_install_completed source={source}");
                ScheduleOcrWarmUp("ocr_dependencies_installed", delayMs: 250);
                return;
            }

            SetStatus(T("OcrEnvironmentInstallFailedShort"));
            AddRealtimeDebugLog($"ocr_dependency_install_failed source={source} error=\"{CleanLog(result.Message)}\"");
            MessageBox.Show(
                this,
                result.Message,
                T("OcrEnvironmentInstallFailedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _isOcrDependencyInstallRunning = false;
            InstallOcrDependenciesMainButton.Content = T("OcrEnvironmentSetupButton");
            UpdateOcrEnvironmentSetupButtonVisibility();
            UpdateAutoButtons();
        }
    }

    private async Task<OcrDependencyInstallResult> RetryOcrDependencyInstallAfterFailureAsync(
        OcrDependencyInstallResult result,
        IProgress<string> progress,
        IProgress<OcrDependencyInstallProgress> detailedProgress,
        string source)
    {
        var currentDirectory = PythonEnvironmentService.ResolveOcrEnvironmentDirectory(_config.OcrConfig);
        var selectedDirectory = OcrEnvironmentInstallRecovery.PromptForAlternativeDirectory(
            this,
            _config.UiLanguage,
            currentDirectory,
            result.Message);

        if (!string.IsNullOrWhiteSpace(selectedDirectory))
        {
            _config.OcrConfig.OcrEnvironmentDirectory = selectedDirectory;
            _config.HasCompletedOcrEnvironmentSetup = false;
            _configService.Save(_config);
            AddRealtimeDebugLog($"ocr_dependency_install_retry source={source} changed_environment_directory=true");
            SetStatus($"正在使用新的 OCR 环境位置重试：{selectedDirectory}");

            result = await _ocrDependencyInstaller.InstallAllAsync(
                _config.OcrConfig,
                progress,
                CancellationToken.None,
                detailedProgress);
        }

        if (!result.Succeeded && OcrEnvironmentInstallRecovery.ShouldOfferAdministratorRestart(result))
        {
            OcrEnvironmentInstallRecovery.PromptRestartAsAdministrator(this, _config.UiLanguage, result.Message);
        }

        return result;
    }

    private async Task TestOcrAsync()
    {
        TestOcrButton.IsEnabled = false;
        SetStatus(T("StatusTestingOcr"));
        var autoTranslateWasRunning = _isAutoTranslating;
        AddRealtimeDebugLog($"ocr_test_started auto_translate_was_running={autoTranslateWasRunning.ToString().ToLowerInvariant()}");

        try
        {
            await PauseAutoTranslateForOcrTestAsync(autoTranslateWasRunning);

            var report = await RunWithCaptureWindowsHiddenAsync(
                () => _ocrService.CompareEnginesAsync(_config),
                CancellationToken.None);

            var window = new OcrTestWindow(report, _config, _config.UiLanguage)
            {
                Owner = this
            };
            window.ShowDialog();
            SetStatus(T("StatusOcrTestComplete"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, T("OcrTestFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus(FormatT("StatusOcrTestFailed", ex.Message));
        }
        finally
        {
            if (autoTranslateWasRunning && !_isAutoTranslating)
            {
                StartAutoTranslate();
                AddRealtimeDebugLog("auto_translate_resumed_after_ocr_test=true");
            }
            else
            {
                AddRealtimeDebugLog("auto_translate_resumed_after_ocr_test=false");
            }

            TestOcrButton.IsEnabled = true;
        }
    }

    private async Task PauseAutoTranslateForOcrTestAsync(bool autoTranslateWasRunning)
    {
        if (!autoTranslateWasRunning || !_isAutoTranslating)
        {
            AddRealtimeDebugLog("auto_translate_paused_for_ocr_test=false");
            return;
        }

        _isAutoTranslating = false;
        _pendingMessageStabilizer.Clear();
        UpdateAutoButtons();
        UpdateRealtimeOcrDebug(state => state.RunningStatus = "paused_for_ocr_test");
        AddRealtimeDebugLog("auto_translate_paused_for_ocr_test=true");
        SetStatus(T("StatusTestingOcr"));
        var stopResult = await _autoOcrCoordinator.StopAsync(TimeSpan.FromSeconds(10));
        _autoTranslateTask = stopResult.PreviousTask;
        ResetAutoOcrWorkerState("pause_for_ocr_test", resetWorkers: false);
        if (!stopResult.Completed)
        {
            AddRealtimeDebugLog("auto_translate_paused_for_ocr_test=true wait_timeout=true");
        }
    }

    private async Task StopAutoTranslateForRestartAsync()
    {
        if (!_isAutoTranslating && !_autoOcrCoordinator.IsRunning)
        {
            return;
        }

        _isAutoTranslating = false;
        _pendingMessageStabilizer.Clear();
        UpdateAutoButtons();
        UpdateRealtimeOcrDebug(state => state.RunningStatus = "stopped");
        AddRealtimeDebugLog("skipped: worker stopped reason=restart");
        SetStatus(T("StatusRestarting"));

        var stopResult = await _autoOcrCoordinator.StopAsync(TimeSpan.FromSeconds(8));
        _autoTranslateTask = stopResult.PreviousTask;
        ResetAutoOcrWorkerState("restart_auto", resetWorkers: false);
        if (!stopResult.Completed)
        {
            AddRealtimeDebugLog("restart_auto wait_timeout=true");
        }
    }

    private void ClearTranslationState(bool clearOverlay)
    {
        _chatDeduper.Clear();
        _pendingMessageStabilizer.Clear();
        _pendingOutputItems.Clear();
        _recentOwnOutputTexts.Clear();
        _ocrTextMaskTrigger.Reset();

        if (clearOverlay)
        {
            _overlayWindow.ClearMessages();
        }
    }

    private async Task<bool> RunOcrCleanTranslateOnceAsync(
        CancellationToken cancellationToken,
        bool forceFlushPending,
        bool includePreviouslyTranslated = false,
        Bitmap? cachedFrame = null,
        string source = "manual",
        long captureMs = 0,
        long cropMs = 0,
        long maskMs = 0,
        int dirtyLineCount = 0,
        string? fullRescanReason = null,
        bool? usedFullOcr = null,
        bool? usedRecognitionOnly = null,
        string? recognitionOnlyReason = null,
        bool? localRegionFullOcr = null,
        int changedPixels = 0,
        double dirtyRegionRatio = 0,
        string? cropRegions = null,
        bool? textMaskChanged = null)
    {
        SetStatus(T("StatusRecognizing"));
        _lastOcrCycleCommitReason = "failed_not_committed";

        var total = Stopwatch.StartNew();
        var ocr = Stopwatch.StartNew();
        var ocrResult = await RecognizeForOcrCycleAsync(cachedFrame, source, cancellationToken);
        ocr.Stop();
        ocrResult = ocrResult with
        {
            Diagnostics = EnrichOcrDiagnostics(
                ocrResult.Diagnostics,
                captureMs,
                cropMs,
                maskMs: maskMs,
                dirtyLineCount: dirtyLineCount,
                fullRescanReason: fullRescanReason,
                usedFullOcr: usedFullOcr,
                usedRecognitionOnly: usedRecognitionOnly,
                recognitionOnlyReason: recognitionOnlyReason,
                localRegionFullOcr: localRegionFullOcr,
                changedPixels: changedPixels,
                dirtyRegionRatio: dirtyRegionRatio,
                cropRegions: cropRegions,
                textMaskChanged: textMaskChanged)
        };
        LogOcrRawText(ocrResult.Lines, ocrResult.Diagnostics);
        var rawOcrLineCount = ocrResult.Lines.Count;
        var selfOcrFilter = FilterSelfOcrLines(ocrResult);
        if (selfOcrFilter.SkippedLines.Count > 0)
        {
            foreach (var skippedLine in selfOcrFilter.SkippedLines)
            {
                AddRealtimeDebugLog($"skipped: self_ocr_suspected raw=\"{FormatRealtimeLogText(skippedLine)}\" skipped_reason=self_ocr_suspected");
            }

        }

        ocrResult = ocrResult with
        {
            Lines = selfOcrFilter.AcceptedLines,
            TextLines = selfOcrFilter.AcceptedTextLines
        };
        var mergeResult = OcrLineContinuationMerger.Merge(ocrResult.Lines, ocrResult.TextLines);
        LogOcrLineContinuationMerge(mergeResult, rawOcrLineCount);
        ocrResult = ocrResult with
        {
            Lines = mergeResult.Lines,
            TextLines = null
        };

        var ocrElapsedMs = ocr.ElapsedMilliseconds;
        var ocrLines = ocrResult.Lines;
        UpdateRealtimeOcrDebug(state =>
        {
            state.LastOcrElapsedMs = ocrElapsedMs;
            state.LastOcrResultLineCount = ocrLines.Count;
        });
        cancellationToken.ThrowIfCancellationRequested();
        if (selfOcrFilter.AcceptedLines.Count == 0 && selfOcrFilter.SkippedLines.Count > 0)
        {
            total.Stop();
            SetRealtimeSkip("self_ocr_suspected");
            UpdateRealtimeOcrDebug(state =>
            {
                state.LastElapsedMs = total.ElapsedMilliseconds;
                state.LastTranslationStatus = "self_ocr_suspected";
            });
            AddRealtimeDebugLog($"skipped: self_ocr_suspected elapsed={ocrElapsedMs}ms {FormatOcrDiagnosticsForLog(ocrResult.Diagnostics)} skipped_reason=self_ocr_suspected");
            RecordOcrTiming(
                captureMs,
                cropMs,
                maskMs,
                ocrResult.Diagnostics?.PreprocessMs ?? 0,
                ocrResult.Diagnostics?.OcrDetectMs,
                ocrResult.Diagnostics?.OcrRecognizeMs,
                ocrResult.Diagnostics?.OcrTotalMs ?? ocrElapsedMs,
                0,
                0,
                total.ElapsedMilliseconds);
            return false;
        }

        if (TryGetOcrSkipReason(ocrResult, out var ocrSkipReason))
        {
            total.Stop();
            SetRealtimeSkip(ocrSkipReason);
            UpdateRealtimeOcrDebug(state =>
            {
                state.LastElapsedMs = total.ElapsedMilliseconds;
                state.LastTranslationStatus = ocrSkipReason;
            });
            AddRealtimeDebugLog($"skipped: {ocrSkipReason} elapsed={ocrElapsedMs}ms {FormatOcrDiagnosticsForLog(ocrResult.Diagnostics)} skipped_reason={CleanLog(ocrSkipReason)}");
            SetStatus(ocrSkipReason.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                ? T("StatusOcrTimeoutSkipped")
                : T("OcrBusy"));
            RecordOcrTiming(
                captureMs,
                cropMs,
                maskMs,
                ocrResult.Diagnostics?.PreprocessMs ?? 0,
                ocrResult.Diagnostics?.OcrDetectMs,
                ocrResult.Diagnostics?.OcrRecognizeMs,
                ocrResult.Diagnostics?.OcrTotalMs ?? ocrElapsedMs,
                0,
                0,
                total.ElapsedMilliseconds);
            return false;
        }

        AddRealtimeDebugLog($"ocr_result lines={ocrLines.Count} elapsed={ocrElapsedMs}ms {FormatOcrDiagnosticsForLog(ocrResult.Diagnostics)}");

        var currentPlayers = ocrLines.Count > 0
            ? await _liveClientDataService.GetCurrentPlayersAsync()
            : [];
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureSelfPlayerNameAsync();
        cancellationToken.ThrowIfCancellationRequested();

        var parse = Stopwatch.StartNew();
        var parsedMessages = _chatCleaner.CleanMessages(
            mergeResult.MergedLines,
            _config,
            currentPlayers,
            _playerNameMatcher);
        AddRealtimeDebugLog($"[AutoOcr][ParsedMessages] {parsedMessages.Count}");
        foreach (var parsedMessage in parsedMessages)
        {
            LogTextPipelineStage("chat_cleaner", parsedMessage.Message);
        }

        var cleanedMessages = FilterExcludedPlayers(parsedMessages);
        var excludedPlayerOnly = parsedMessages.Count > 0 && cleanedMessages.Count == 0;
        var stableMessages = GetStableMessages(cleanedMessages, forceFlushPending, source);
        parse.Stop();

        var translatedCount = 0;
        var duplicateCount = 0;
        var translationFailedCount = 0;
        var untranslatedOutputCount = 0;
        var translate = Stopwatch.StartNew();

        var orderedStableMessages = stableMessages
            .OrderBy(message => message.SourceOrder)
            .ThenBy(message => message.Timestamp ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(message => message.SourceRawLineIndex)
            .ToList();
        var candidateMessages = new List<CleanedChatMessage>();
        foreach (var message in orderedStableMessages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!includePreviouslyTranslated && _config.OcrConfig.EnableLineDeduplication)
            {
                var dedupeDecision = _chatDeduper.Probe(message);
                cancellationToken.ThrowIfCancellationRequested();
                AddChatDedupeDebugLog(dedupeDecision);
                if (!dedupeDecision.ShouldTranslate)
                {
                    duplicateCount++;
                    ChatDeduper.CommitDuplicate(dedupeDecision);
                    AddRealtimeDebugLog($"[AutoOcr][Skipped] reason={dedupeDecision.Reason}");
                    LogOutputPipeline(
                        message,
                        dedupeDecision.NormalizedMessage,
                        ResolveExistingOutputMessageId(message, dedupeDecision.NormalizedMessage),
                        "skip_duplicate",
                        string.Empty,
                        dedupeDecision.Reason);
                    continue;
                }
            }

            candidateMessages.Add(message);
        }

        var translationBatch = new TranslationBatchService(_config, _messageNormalizer, _translateService);
        var queuedOutputs = new List<QueuedTranslationOutput>();
        foreach (var job in translationBatch.BuildJobs(candidateMessages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogTextPipelineStage("ocr_text_fixer", OcrTextFixer.ApplyBuiltInFixes(job.Message.Message));
            LogTextPipelineStage("message_normalizer", job.NormalizedText);
            AddGlossaryDebugLog(job.NormalizedMessage);
            if (ChatCleaner.IsInvalidMessage(job.NormalizedText))
            {
                SetRealtimeSkip("no_valid_chat");
                continue;
            }

            var outputKey = BuildOutputKey(job.Message, job.NormalizedText);
            if (_pendingOutputItems.TryGetValue(outputKey, out var existingOutput)
                && existingOutput.Status is OutputItemStatus.Pending or OutputItemStatus.Done)
            {
                duplicateCount++;
                LogOutputPipeline(
                    job.Message,
                    job.NormalizedText,
                    existingOutput.MessageId,
                    "skip_duplicate",
                    existingOutput.DisplayedText,
                    $"status={existingOutput.Status}");
                continue;
            }

            var outputItem = CreatePendingOutputItem(outputKey, job.Message, job.NormalizedText);
            var partialReplacement = TryAttachPartialOutputReplacement(job.Message, job.NormalizedText, outputItem);
            LogOutputPipeline(
                job.Message,
                job.NormalizedText,
                outputItem.MessageId,
                partialReplacement is null ? "create_pending" : "reuse_partial_overlay",
                partialReplacement?.DisplayedText ?? string.Empty,
                partialReplacement is null ? null : $"partial_source={partialReplacement.NormalizedSourceText}");
            LogTextPipelineStage("translator_input", job.NormalizedText);
            AddRealtimeDebugLog($"[AutoOcr][TranslateQueued] {FormatRealtimeLogText(job.NormalizedText)}");
            AddRealtimeDebugLog($"translate queued text=\"{FormatRealtimeLogText(job.NormalizedText)}\"");
            queuedOutputs.Add(new QueuedTranslationOutput(job, outputKey, outputItem));
        }

        UpdateRealtimeOcrDebug(state => state.LastTranslationStatus = queuedOutputs.Count > 0 ? "translating" : state.LastTranslationStatus);
        var translationResults = await translationBatch.TranslateAsync(queuedOutputs.Select(item => item.Job).ToList(), cancellationToken);
        var resultByBatchIndex = translationResults.ToDictionary(result => result.Job.BatchIndex);
        var newOverlayMessages = new List<OverlayMessageRequest>();
        var newOverlayContexts = new List<(QueuedTranslationOutput Context, TranslationResult Result)>();

        foreach (var context in queuedOutputs
                     .OrderBy(item => item.Job.SourceOrder)
                     .ThenBy(item => item.Job.Message.Timestamp ?? string.Empty, StringComparer.Ordinal)
                     .ThenBy(item => item.Job.BatchIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!resultByBatchIndex.TryGetValue(context.Job.BatchIndex, out var result))
            {
                translationFailedCount++;
                MarkOutputTranslationFailed(context.OutputKey);
                continue;
            }

            if (!result.Success || string.IsNullOrWhiteSpace(result.OutputText))
            {
                if (string.Equals(result.ErrorKind, "untranslated_output", StringComparison.OrdinalIgnoreCase))
                {
                    untranslatedOutputCount++;
                    AddRealtimeDebugLog($"skipped: untranslated_output source=\"{FormatRealtimeLogText(context.Job.NormalizedText)}\" result=\"{FormatRealtimeLogText(result.RawOutputText ?? string.Empty)}\"");
                }
                else
                {
                    translationFailedCount++;
                }

                MarkOutputTranslationFailed(context.OutputKey);
                LogOutputPipeline(
                    context.Job.Message,
                    context.Job.NormalizedText,
                    context.OutputItem.MessageId,
                    "translation_failed",
                    string.Empty,
                    result.ErrorKind ?? "translation_failed");
                continue;
            }

            AddRealtimeDebugLog($"translate_done elapsed={translate.ElapsedMilliseconds}ms source_order={context.Job.SourceOrder}");
            if (context.OutputItem.OverlayMessageId == Guid.Empty)
            {
                newOverlayMessages.Add(new OverlayMessageRequest(
                    BuildOriginalDisplayText(context.Job.Message),
                    result.OutputText,
                    context.Job.Message.Channel));
                newOverlayContexts.Add((context, result));
            }
            else
            {
                await UpdateOverlayTranslationAsync(context.OutputItem.OverlayMessageId, result.OutputText);
                CompleteSuccessfulOutput(context, result.OutputText, context.OutputItem.OverlayMessageId, ref translatedCount);
            }
        }

        if (newOverlayMessages.Count > 0)
        {
            var overlayIds = await AddOverlayMessagesAsync(newOverlayMessages);
            for (var index = 0; index < newOverlayContexts.Count; index++)
            {
                var (context, result) = newOverlayContexts[index];
                var overlayId = index < overlayIds.Count ? overlayIds[index] : Guid.Empty;
                CompleteSuccessfulOutput(context, result.OutputText!, overlayId, ref translatedCount);
            }
        }

        translate.Stop();
        total.Stop();
        ocrResult = ocrResult with
        {
            Diagnostics = EnrichOcrDiagnostics(
                ocrResult.Diagnostics,
                captureMs,
                cropMs,
                total.ElapsedMilliseconds,
                maskMs,
                dirtyLineCount,
                fullRescanReason,
                usedFullOcr,
                usedRecognitionOnly,
                recognitionOnlyReason,
                localRegionFullOcr,
                changedPixels,
                dirtyRegionRatio,
                cropRegions,
                textMaskChanged,
                translateMs: translate.ElapsedMilliseconds,
                postProcessMs: parse.ElapsedMilliseconds,
                cycleTotalMs: total.ElapsedMilliseconds)
        };
        var finalSkipReason = ResolveFinalSkipReason(
            ocrLines.Count,
            cleanedMessages.Count,
            stableMessages.Count,
            translatedCount,
            duplicateCount,
            translationFailedCount,
            untranslatedOutputCount,
            excludedPlayerOnly);
        if (translatedCount == 0)
        {
            SetRealtimeSkip(finalSkipReason);
            AddRealtimeDebugLog($"skipped: {finalSkipReason} skipped_reason={finalSkipReason}");
        }

        UpdateRealtimeOcrDebug(state =>
        {
            state.LastElapsedMs = total.ElapsedMilliseconds;
            state.LastTranslationStatus = translatedCount > 0 ? "done" : finalSkipReason;
            if (translatedCount > 0)
            {
                state.LastSkipReason = "-";
            }
        });
        RecordOcrTiming(
            captureMs,
            cropMs,
            maskMs,
            ocrResult.Diagnostics?.PreprocessMs ?? 0,
            ocrResult.Diagnostics?.OcrDetectMs,
            ocrResult.Diagnostics?.OcrRecognizeMs,
            ocrResult.Diagnostics?.OcrTotalMs ?? ocrElapsedMs,
            parse.ElapsedMilliseconds,
            translate.ElapsedMilliseconds,
            total.ElapsedMilliseconds);
        LogOcrCycle(ocrResult, cleanedMessages, stableMessages.Count, currentPlayers.Count, translatedCount, duplicateCount);
        SetStatus(BuildOcrStatusMessage(
            _config.UiLanguage,
            ocrResult,
            cleanedMessages,
            stableMessages.Count,
            currentPlayers.Count,
            translatedCount,
            duplicateCount));
        var shouldCommitFrame = translatedCount > 0 || duplicateCount > 0;
        _lastOcrCycleCommitReason = translatedCount > 0
            ? "ocr_success"
            : duplicateCount > 0
                ? "duplicate_processed"
                : finalSkipReason;
        return shouldCommitFrame;
    }

    private static string ResolveFinalSkipReason(
        int ocrLineCount,
        int cleanedMessageCount,
        int stableMessageCount,
        int translatedCount,
        int duplicateCount,
        int translationFailedCount,
        int untranslatedOutputCount,
        bool excludedPlayerOnly)
    {
        if (translatedCount > 0)
        {
            return "done";
        }

        if (ocrLineCount == 0)
        {
            return "no_ocr_lines";
        }

        if (cleanedMessageCount == 0)
        {
            return excludedPlayerOnly ? "excluded_player_only" : "no_valid_chat";
        }

        if (stableMessageCount == 0)
        {
            return "pending_unstable";
        }

        if (translationFailedCount > 0)
        {
            return "translation_failed";
        }

        if (untranslatedOutputCount > 0)
        {
            return "untranslated_output";
        }

        if (duplicateCount >= stableMessageCount)
        {
            return "duplicate_only";
        }

        return "no_new_translation";
    }

    private List<CleanedChatMessage> FilterExcludedPlayers(IEnumerable<CleanedChatMessage> messages)
    {
        var filtered = new List<CleanedChatMessage>();

        foreach (var message in messages)
        {
            var decision = PlayerExclusionService.IsPlayerExcluded(message, _config.TranslateConfig);
            if (decision.Excluded)
            {
                ChatDeduper.WriteSkippedValidChatLog(message, $"excluded_player:{decision.Reason}");
                LogExcludedPlayerSkip(decision);
                continue;
            }

            filtered.Add(message);
        }

        return filtered;
    }

    private void LogExcludedPlayerSkip(PlayerExclusionDecision decision)
    {
        var line = $"excluded_player_skip {PlayerExclusionService.FormatDebugLog(decision)}";
        AddRealtimeDebugLog(line);
        PlayerExclusionService.WriteDebugLog(decision);
    }

    private async Task<OcrRecognitionResult> RecognizeForOcrCycleAsync(
        Bitmap? cachedFrame,
        string source,
        CancellationToken cancellationToken)
    {
        if (cachedFrame is null)
        {
            return await RecognizeChatLinesWithoutOverlayAsync(cancellationToken);
        }

        if (source.Equals("manual", StringComparison.OrdinalIgnoreCase))
        {
            return await RecognizeCachedChatFrameAsync(cachedFrame, cancellationToken);
        }

        var workerReady = _ocrService.IsWorkerReady(_config);
        var autoTimeoutMs = ResolveAutoOcrTimeoutMs(workerReady);
        AddRealtimeDebugLog($"ocr_start source={source} timeout={autoTimeoutMs}ms worker_ready={workerReady}");
        if (!TryEnterAutoOcrSlot(autoTimeoutMs, out var slotGeneration, out var busyResult))
        {
            return busyResult;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(autoTimeoutMs);
        using var ocrBitmap = (Bitmap)cachedFrame.Clone();

        try
        {
            return await RecognizeCachedChatFrameAsync(ocrBitmap, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            var reason = workerReady ? "ocr_timeout" : "ocr_cold_start_timeout";
            SetRealtimeSkip(reason);
            AddRealtimeDebugLog($"skipped: {reason} timeout={autoTimeoutMs}ms skipped_reason={reason}");
            return BuildOcrSkipResult($"OCR timeout after {autoTimeoutMs}ms.", reason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw;
        }
        finally
        {
            ReleaseAutoOcrSlotIfCurrent(slotGeneration);
        }
    }

    private bool TryEnterAutoOcrSlot(int autoTimeoutMs, out int slotGeneration, out OcrRecognitionResult busyResult)
    {
        slotGeneration = 0;
        busyResult = null!;
        var now = DateTimeOffset.UtcNow;
        if (Interlocked.CompareExchange(ref _autoOcrInFlight, 1, 0) == 0)
        {
            slotGeneration = Interlocked.Increment(ref _autoOcrGeneration);
            _autoOcrInFlightStartedAt = now;
            return true;
        }

        var startedAt = _autoOcrInFlightStartedAt;
        var busyFor = startedAt == DateTimeOffset.MinValue ? TimeSpan.Zero : now - startedAt;
        var staleAfter = TimeSpan.FromMilliseconds(Math.Max(autoTimeoutMs + 1000, AutoOcrMinimumTimeoutMs + 1000));
        if (startedAt != DateTimeOffset.MinValue && busyFor > staleAfter)
        {
            AddRealtimeDebugLog($"ocr_busy_stale_reset busy_for_ms={busyFor.TotalMilliseconds:0}");
            ResetAutoOcrWorkerState("stale_ocr_busy", resetWorkers: false);
            if (Interlocked.CompareExchange(ref _autoOcrInFlight, 1, 0) == 0)
            {
                slotGeneration = Interlocked.Increment(ref _autoOcrGeneration);
                _autoOcrInFlightStartedAt = now;
                return true;
            }
        }

        SetRealtimeSkip("ocr_busy");
        AddRealtimeDebugLog($"skipped: ocr_busy existing_task_running busy_for_ms={busyFor.TotalMilliseconds:0} skipped_reason=ocr_busy");
        busyResult = BuildOcrSkipResult("OCR already running; skipped this frame.", "ocr_busy");
        return false;
    }

    private void ReleaseAutoOcrSlotIfCurrent(int slotGeneration)
    {
        if (Volatile.Read(ref _autoOcrGeneration) != slotGeneration)
        {
            return;
        }

        _autoOcrInFlightStartedAt = DateTimeOffset.MinValue;
        Interlocked.Exchange(ref _autoOcrInFlight, 0);
    }

    private OcrRecognitionResult BuildOcrSkipResult(string message, string reason)
    {
        return new OcrRecognitionResult(
            [],
            OcrEngines.Normalize(_config.OcrConfig.OcrEngine),
            CaptureSucceeded: true,
            message,
            Diagnostics: new OcrRunDiagnostics(
                OcrTotalMs: 0,
                TotalMs: 0,
                Backend: "skipped",
                Mode: OcrMode.Normalize(_config.OcrConfig.OcrMode),
                SelectedLanguage: OcrLanguages.Normalize(_config.OcrConfig.OcrLanguage),
                Parameters: $"reason={reason}"));
    }

    private void ResetAutoOcrWorkerState(string reason, bool resetWorkers = true)
    {
        _autoOcrInFlightStartedAt = DateTimeOffset.MinValue;
        Interlocked.Increment(ref _autoOcrGeneration);
        Interlocked.Exchange(ref _autoOcrInFlight, 0);
        if (!resetWorkers)
        {
            AddRealtimeDebugLog($"ocr_state_reset reason={reason}");
            return;
        }

        try
        {
            _ocrService.ResetWorkers();
            AddRealtimeDebugLog($"ocr_worker_reset reason={reason}");
        }
        catch (Exception ex)
        {
            AddRealtimeDebugLog($"ocr_worker_reset_failed reason={reason} error=\"{CleanLog(ex.Message)}\"");
        }
    }

    private int ResolveAutoOcrTimeoutMs(bool workerReady)
    {
        var configuredTimeoutMs = Math.Max(AutoOcrMinimumTimeoutMs, _config.OcrConfig.OcrTimeoutMs);
        return workerReady
            ? configuredTimeoutMs
            : Math.Max(configuredTimeoutMs, AutoOcrColdStartTimeoutMs);
    }

    private async Task<OcrRecognitionResult> RecognizeChatLinesWithoutOverlayAsync(CancellationToken cancellationToken)
    {
        return await RunWithCaptureWindowsHiddenAsync(
            () => _ocrService.RecognizeChatLinesWithDiagnosticsAsync(_config, cancellationToken),
            cancellationToken);
    }

    private async Task<OcrRecognitionResult> RecognizeCachedChatFrameAsync(Bitmap cachedFrame, CancellationToken cancellationToken)
    {
        return await _ocrService.RecognizeChatLinesWithDiagnosticsAsync(_config, cachedFrame, cancellationToken);
    }

    private async Task<Bitmap?> CaptureChatFrameWithoutOverlayAsync(CancellationToken cancellationToken, bool bottomOnly = false)
    {
        // bottomOnly is intentionally not applied: automatic OCR must keep using the user's full selected region.
        if (bottomOnly)
        {
            AppLogService.AppendVerboseText(
                "ocr-region-policy.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} bottom_only_request_ignored policy=full_user_selected_region{Environment.NewLine}");
        }

        return await RunWithCaptureWindowsHiddenAsync(
            () => Task.Run(
                () => OcrService.CaptureConfiguredRegion(_config.OcrConfig),
                cancellationToken),
            cancellationToken);
    }

    private async Task<T> RunWithCaptureWindowsHiddenAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        if (!Dispatcher.CheckAccess())
        {
            var operation = Dispatcher.InvokeAsync(
                () => RunWithCaptureWindowsHiddenAsync(action, cancellationToken),
                DispatcherPriority.Send,
                cancellationToken);
            return await await operation.Task;
        }

        var captureRect = GetCaptureRegionRect();
        var overlayBounds = TryGetWindowScreenBounds(_overlayWindow);
        var overlayIntersectsCaptureRegion = RectIntersects(overlayBounds, captureRect);
        var hideWindowsBeforeCapture = _config.OverlayConfig.HideOverlayDuringCapture;
        var hiddenWindows = new List<HiddenWindowState>();

        try
        {
            if (overlayIntersectsCaptureRegion && !_overlayWindow.IsExcludedFromCapture)
            {
                SetStatus("翻译悬浮窗与 OCR 识别区域重叠，可能导致重复识别。建议移动悬浮窗，或开启截图时排除悬浮窗。");
            }

            if (hideWindowsBeforeCapture)
            {
                hiddenWindows = HideInterferingWindowsForCapture(captureRect);
                if (hiddenWindows.Count > 0)
                {
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    await Task.Delay(CaptureHideDelayMs, cancellationToken);
                }
            }

            LogCaptureContext(
                captureRect,
                overlayBounds,
                overlayIntersectsCaptureRegion,
                hideWindowsBeforeCapture,
                hiddenWindows.Count,
                skippedReason: "<none>");

            return await action();
        }
        finally
        {
            RestoreHiddenWindows(hiddenWindows);
        }
    }

    private List<HiddenWindowState> HideInterferingWindowsForCapture(Rect captureRect)
    {
        var hidden = new List<HiddenWindowState>();
        foreach (Window window in Application.Current.Windows)
        {
            if (!ShouldHideWindowForCapture(window, captureRect))
            {
                continue;
            }

            hidden.Add(new HiddenWindowState(window));
            window.Hide();
        }

        return hidden;
    }

    private bool ShouldHideWindowForCapture(Window window, Rect captureRect)
    {
        if (!window.IsVisible || window.ActualWidth <= 0 || window.ActualHeight <= 0)
        {
            return false;
        }

        var bounds = TryGetWindowScreenBounds(window);
        var intersectsCapture = RectIntersects(bounds, captureRect);
        if (!intersectsCapture)
        {
            return false;
        }

        if (ReferenceEquals(window, _overlayWindow))
        {
            return false;
        }

        if (ReferenceEquals(window, this))
        {
            return true;
        }

        if (window.Topmost || window.AllowsTransparency || window.Opacity < 0.999 || window.WindowStyle == WindowStyle.None)
        {
            return true;
        }

        return IsLayeredOrTopMostWindow(window);
    }

    private static void RestoreHiddenWindows(IEnumerable<HiddenWindowState> hiddenWindows)
    {
        foreach (var state in hiddenWindows.Reverse())
        {
            try
            {
                if (!state.Window.IsVisible)
                {
                    state.Window.Show();
                }
            }
            catch
            {
                // A window may have closed while OCR was running.
            }
        }
    }

    private Rect GetCaptureRegionRect()
    {
        var ocr = _config.OcrConfig;
        return new Rect(
            ocr.RegionX,
            ocr.RegionY,
            Math.Max(1, ocr.RegionWidth),
            Math.Max(1, ocr.RegionHeight));
    }

    private static Rect? TryGetWindowScreenBounds(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var nativeRect))
            {
                return new Rect(
                    nativeRect.Left,
                    nativeRect.Top,
                    Math.Max(1, nativeRect.Right - nativeRect.Left),
                    Math.Max(1, nativeRect.Bottom - nativeRect.Top));
            }

            var topLeft = window.PointToScreen(new Point(0, 0));
            var bottomRight = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
            return new Rect(topLeft, bottomRight);
        }
        catch
        {
            return null;
        }
    }

    private static bool RectIntersects(Rect? first, Rect second)
    {
        return first is { } rect && rect.IntersectsWith(second);
    }

    private static bool IsLayeredOrTopMostWindow(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var style = GetWindowLong(hwnd, GwlExStyle);
            return (style & WsExLayered) != 0 || (style & WsExTopMost) != 0;
        }
        catch
        {
            return false;
        }
    }

    private void LogCaptureContext(
        Rect captureRect,
        Rect? overlayBounds,
        bool overlayIntersectsCaptureRegion,
        bool hideWindowsBeforeCapture,
        int hiddenWindowsCount,
        string skippedReason)
    {
        var line =
            $"capture_region={FormatRectForLog(captureRect)} " +
            $"overlay_bounds={FormatRectForLog(overlayBounds)} " +
            $"overlay_intersects_capture_region={FormatBool(overlayIntersectsCaptureRegion)} " +
            $"hide_overlay_before_capture={FormatBool(hideWindowsBeforeCapture)} " +
            $"hidden_windows_count={hiddenWindowsCount} " +
            $"skipped_reason={CleanLog(skippedReason)}";

        AddRealtimeDebugLog($"capture_context {line}");
        AppLogService.AppendVerboseText("capture-pipeline-debug.log", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
    }

    private async Task TranslateReplyInputAndCopyAsync()
    {
        var text = ReplyInputTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            ClipboardStatusText.Text = T("ClipboardInputRequired");
            ReplyOutputTextBox.Text = string.Empty;
            return;
        }

        await TranslateTextAndCopyAsync(text, GetSelectedReplyTargetLanguage());
    }

    private async Task TranslateClipboardTextAndCopyAsync()
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                ClipboardStatusText.Text = T("ClipboardNoText");
                ReplyOutputTextBox.Text = string.Empty;
                return;
            }

            var text = Clipboard.GetText().Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                ClipboardStatusText.Text = T("ClipboardTextEmpty");
                ReplyOutputTextBox.Text = string.Empty;
                return;
            }

            await TranslateTextAndCopyAsync(text, GetSelectedReplyTargetLanguage());
        }
        catch (Exception ex)
        {
            ClipboardStatusText.Text = FormatT("ClipboardReadFailed", ex.Message);
            ReplyOutputTextBox.Text = string.Empty;
        }
    }

    private async Task TranslateTextAndCopyAsync(string text, string targetLanguage)
    {
        TranslateReplyButton.IsEnabled = false;

        try
        {
            ClipboardStatusText.Text = T("TranslatingEllipsis");
            ReplyOutputTextBox.Text = T("TranslatingEllipsis");
            var translatedText = await TranslateTextAsync(text, targetLanguage);

            Clipboard.SetText(translatedText);
            ReplyOutputTextBox.Text = translatedText;
            ClipboardStatusText.Text = T("ClipboardCopied");
        }
        catch (Exception ex)
        {
            ClipboardStatusText.Text = FormatT("TranslateCopyFailed", ex.Message);
            ReplyOutputTextBox.Text = string.Empty;
        }
        finally
        {
            TranslateReplyButton.IsEnabled = true;
        }
    }

    private async Task<string> TranslateTextAsync(string text, string targetLanguage, CancellationToken cancellationToken = default)
    {
        var normalizedMessage = _messageNormalizer.Normalize(
            text,
            _config.TranslateConfig.ToxicDisplayMode,
            targetLanguage);
        var translatedText = normalizedMessage.ShouldBypassTranslator
            ? normalizedMessage.DirectTranslation!
            : await _translateService.TranslateAsync(normalizedMessage.NormalizedText, targetLanguage, "auto", cancellationToken);
        return TranslationOutputValidator.TryBuildDisplayTranslation(
                normalizedMessage.NormalizedText,
                translatedText,
                targetLanguage,
                normalizedMessage.IsTrustedDirectOutput,
                out var displayTranslation)
            ? displayTranslation
            : string.Empty;
    }

    private static bool TryBuildDisplayTranslation(
        string sourceText,
        string translatedText,
        string targetLanguage,
        bool allowTrustedDirectOutput,
        out string displayTranslation)
    {
        return TranslationOutputValidator.TryBuildDisplayTranslation(
            sourceText,
            translatedText,
            targetLanguage,
            allowTrustedDirectOutput,
            out displayTranslation);
    }

    private PendingOutputItem CreatePendingOutputItem(
        string outputKey,
        CleanedChatMessage message,
        string normalizedSourceText)
    {
        PruneOutputItems();
        var item = new PendingOutputItem(
            BuildMessageId(outputKey),
            outputKey,
            BuildOutputScopeKey(message),
            message.Message,
            normalizedSourceText,
            DateTimeOffset.UtcNow);
        _pendingOutputItems[outputKey] = item;
        return item;
    }

    private PendingOutputItem? TryAttachPartialOutputReplacement(
        CleanedChatMessage message,
        string normalizedSourceText,
        PendingOutputItem newItem)
    {
        var currentKey = NormalizeMessageKey(ChatDeduper.NormalizeMessage(normalizedSourceText));
        if (!IsPartialDuplicateCandidate(currentKey))
        {
            return null;
        }

        var scopeKey = BuildOutputScopeKey(message);
        var now = DateTimeOffset.UtcNow;
        var existing = _pendingOutputItems.Values
            .Where(item => item.Status == OutputItemStatus.Done
                && item.OverlayMessageId != Guid.Empty
                && item.ScopeKey.Equals(scopeKey, StringComparison.OrdinalIgnoreCase)
                && now - item.UpdatedAt <= TimeSpan.FromSeconds(8))
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault(item =>
            {
                var existingKey = NormalizeMessageKey(ChatDeduper.NormalizeMessage(item.NormalizedSourceText));
                return IsPartialDuplicateCandidate(existingKey)
                    && IsStrictStablePrefix(existingKey, currentKey);
            });

        if (existing is null)
        {
            return null;
        }

        newItem.OverlayMessageId = existing.OverlayMessageId;
        newItem.DisplayedText = existing.DisplayedText;
        AddRealtimeDebugLog($"partial_duplicate replacement partial=\"{FormatRealtimeLogText(existing.NormalizedSourceText)}\" full=\"{FormatRealtimeLogText(normalizedSourceText)}\"");
        return existing;
    }

    private void CompleteOutputItem(string outputKey, Guid overlayMessageId, string displayedText)
    {
        if (!_pendingOutputItems.TryGetValue(outputKey, out var item))
        {
            return;
        }

        item.Status = OutputItemStatus.Done;
        item.OverlayMessageId = overlayMessageId;
        item.DisplayedText = displayedText;
        item.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private void MarkOutputTranslationFailed(string outputKey)
    {
        if (!_pendingOutputItems.TryGetValue(outputKey, out var item))
        {
            return;
        }

        item.Status = OutputItemStatus.Failed;
        item.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private void CompleteSuccessfulOutput(
        QueuedTranslationOutput context,
        string displayedText,
        Guid overlayMessageId,
        ref int translatedCount)
    {
        if (overlayMessageId == Guid.Empty)
        {
            MarkOutputTranslationFailed(context.OutputKey);
            AddRealtimeDebugLog($"overlay_add_failed source=\"{FormatRealtimeLogText(context.Job.NormalizedText)}\"");
            return;
        }

        CompleteOutputItem(context.OutputKey, overlayMessageId, displayedText);
        RememberOwnOutputText(displayedText, "translation");
        RememberOwnOutputText(BuildOriginalDisplayText(context.Job.Message), "overlay_sender");
        _chatDeduper.CommitSuccess(context.Job.Message);
        LogOutputPipeline(
            context.Job.Message,
            context.Job.NormalizedText,
            context.OutputItem.MessageId,
            "displayed",
            displayedText,
            $"translation_kind={context.Job.Kind};source_order={context.Job.SourceOrder}");
        RememberLastTranslationEvent(context.Job.Message, context.Job.NormalizedText, displayedText);
        translatedCount++;
    }

    private string ResolveExistingOutputMessageId(CleanedChatMessage message, string normalizedSourceText)
    {
        var outputKey = BuildOutputKey(message, normalizedSourceText);
        return _pendingOutputItems.TryGetValue(outputKey, out var item)
            ? item.MessageId
            : BuildMessageId(outputKey);
    }

    private static string BuildOutputKey(CleanedChatMessage message, string normalizedSourceText)
    {
        var identity = BuildOutputScopeKey(message);
        var normalized = ChatDeduper.NormalizeMessage(normalizedSourceText);
        return $"{identity}|normalized|{normalized}";
    }

    private static string BuildOutputScopeKey(CleanedChatMessage message)
    {
        var parsed = ChatDeduper.ParseChatLine(message.RawLine);
        var timestamp = NormalizeStableTimestamp(message.Timestamp ?? parsed.Timestamp);
        var sender = NormalizeParticipantKey(
            message.FixedPlayerName
            ?? message.OcrPlayerName
            ?? parsed.Sender);
        var channel = NormalizeParticipantKey(message.RawChannelText ?? parsed.Channel ?? message.Channel.ToString());
        return $"{channel}|{sender}|{timestamp}";
    }

    private static string BuildMessageId(string outputKey)
    {
        unchecked
        {
            var hash = 14695981039346656037UL;
            foreach (var ch in outputKey)
            {
                hash ^= ch;
                hash *= 1099511628211UL;
            }

            return $"msg_{hash:X16}";
        }
    }

    private void PruneOutputItems()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _pendingOutputItems.Values
                     .Where(item => now - item.UpdatedAt > TimeSpan.FromMinutes(10))
                     .ToList())
        {
            _pendingOutputItems.Remove(item.OutputKey);
        }

        while (_pendingOutputItems.Count > 512)
        {
            var oldest = _pendingOutputItems.Values
                .OrderBy(item => item.UpdatedAt)
                .FirstOrDefault();
            if (oldest is null)
            {
                break;
            }

            _pendingOutputItems.Remove(oldest.OutputKey);
        }
    }

    private static void LogOutputPipeline(
        CleanedChatMessage message,
        string normalizedSourceText,
        string messageId,
        string outputAction,
        string displayedText,
        string? extra = null)
    {
        try
        {
            var line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [OutputPipeline] " +
                $"ocr_text=\"{CleanOutputLog(message.Message)}\" " +
                $"raw_ocr_line=\"{CleanOutputLog(message.RawLine)}\" " +
                $"normalized_source_text=\"{CleanOutputLog(normalizedSourceText)}\" " +
                $"message_id={messageId} " +
                $"output_action={outputAction} " +
                $"displayed_text=\"{CleanOutputLog(displayedText)}\" " +
                $"extra=\"{CleanOutputLog(extra ?? string.Empty)}\"";
            AppLogService.AppendVerboseText("output-pipeline-debug.log", $"{line}{Environment.NewLine}");
        }
        catch
        {
            // Output diagnostics must never interrupt OCR or translation.
        }
    }

    private static string CleanOutputLog(string value)
    {
        var text = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        }

        return text.Length <= 160 ? text : $"{text[..160]}...";
    }

    private void LogOcrRawText(IReadOnlyCollection<string> lines, OcrRunDiagnostics? diagnostics)
    {
        var rawText = lines.Count == 0
            ? "<none>"
            : string.Join(" | ", lines.Select(FormatRealtimeLogText));
        AddRealtimeDebugLog($"ocr_raw_text=\"{rawText}\" ocr_input_image_path=\"{CleanLog(diagnostics?.OcrInputImagePath)}\"");
        AppLogService.AppendVerboseText(
            "capture-pipeline-debug.log",
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ocr_raw_text=\"{CleanOutputLog(string.Join(" | ", lines))}\" ocr_input_image_path=\"{CleanLog(diagnostics?.OcrInputImagePath)}\" skipped_reason=<none>{Environment.NewLine}");
    }

    private SelfOcrFilterResult FilterSelfOcrLines(OcrRecognitionResult result)
    {
        var lines = result.Lines;
        PruneRecentOwnOutputTexts();
        if (lines.Count == 0 || _recentOwnOutputTexts.Count == 0)
        {
            return new SelfOcrFilterResult(lines.ToList(), [], result.TextLines);
        }

        if (!ShouldAllowSelfOcrFiltering())
        {
            return new SelfOcrFilterResult(lines.ToList(), [], result.TextLines);
        }

        var accepted = new List<string>();
        List<OcrTextLine>? acceptedTextLines = result.TextLines is { Count: var textLineCount } && textLineCount == lines.Count
            ? new List<OcrTextLine>()
            : null;
        var skipped = new List<string>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (IsRealPlayerChatLine(line))
            {
                accepted.Add(line);
                acceptedTextLines?.Add(result.TextLines![index]);
                continue;
            }

            if (IsSelfOcrSuspected(line, out var matchedEntry, out var similarity, out var selfOcrReason))
            {
                skipped.Add(line);
                LogSelfOcrSkip(line, matchedEntry, similarity, selfOcrReason);
                continue;
            }

            accepted.Add(line);
            acceptedTextLines?.Add(result.TextLines![index]);
        }

        return new SelfOcrFilterResult(accepted, skipped, acceptedTextLines ?? result.TextLines);
    }

    private bool ShouldAllowSelfOcrFiltering()
    {
        try
        {
            if (!Dispatcher.CheckAccess())
            {
                return Dispatcher.Invoke(ShouldAllowSelfOcrFiltering, DispatcherPriority.Send);
            }

            var captureRect = GetCaptureRegionRect();
            var overlayBounds = TryGetWindowScreenBounds(_overlayWindow);
            var overlayIntersectsCapture = RectIntersects(overlayBounds, captureRect);
            return overlayIntersectsCapture || !_overlayWindow.IsExcludedFromCapture;
        }
        catch
        {
            return false;
        }
    }

    private void LogOcrLineContinuationMerge(OcrLineMergeResult result, int rawOcrLineCount)
    {
        AddRealtimeDebugLog($"ocr_line_merge raw={rawOcrLineCount} accepted={result.RawLineCount} merged={result.MergedLineCount} continuations={result.Events.Count}");
        try
        {
            var builder = new StringBuilder();
            builder.Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ocr_line_merge raw_ocr_count={rawOcrLineCount} accepted_count={result.RawLineCount} merged_count={result.MergedLineCount} continuation_count={result.Events.Count}");
            foreach (var line in result.Lines)
            {
                var parsed = ChatDeduper.ParseChatLine(line);
                if (parsed.MatchedPlayerChatPattern)
                {
                    builder.Append($" merged_body=\"{CleanOutputLog(parsed.Message)}\"");
                }
            }

            foreach (var mergeEvent in result.Events)
            {
                builder.Append($" continuation line_index={mergeEvent.ContinuationLineIndex} before=\"{CleanOutputLog(mergeEvent.BeforeText)}\" append=\"{CleanOutputLog(mergeEvent.ContinuationText)}\" after=\"{CleanOutputLog(mergeEvent.AfterText)}\"");
                if (!string.IsNullOrWhiteSpace(mergeEvent.SplitJoinedToken))
                {
                    builder.Append($" split_join left=\"{mergeEvent.SplitLeftToken}\" right=\"{mergeEvent.SplitRightToken}\" joined=\"{mergeEvent.SplitJoinedToken}\" near_right_edge={mergeEvent.SplitWasNearRightEdge.ToString().ToLowerInvariant()}");
                }
            }

            builder.AppendLine();
            AppLogService.AppendVerboseText("capture-pipeline-debug.log", builder.ToString());
        }
        catch
        {
            // Continuation merge logging must never affect OCR or translation.
        }
    }

    private static void LogTextPipelineStage(string stage, string text)
    {
        try
        {
            AppLogService.AppendVerboseText(
                "capture-pipeline-debug.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} text_stage={stage} text=\"{CleanOutputLog(text)}\"{Environment.NewLine}");
        }
        catch
        {
            // Text diagnostics must never affect OCR or translation.
        }
    }

    private void RememberOwnOutputText(string text, string kind)
    {
        var normalized = NormalizeSelfOcrText(text);
        if (normalized.Length < 3)
        {
            return;
        }

        _recentOwnOutputTexts.Enqueue(new RecentOwnOutputText(text, normalized, kind, DateTimeOffset.UtcNow));
        PruneRecentOwnOutputTexts();
    }

    private void PruneRecentOwnOutputTexts()
    {
        var cutoff = DateTimeOffset.UtcNow - SelfOcrIgnoreTtl;
        while (_recentOwnOutputTexts.Count > 0
               && (_recentOwnOutputTexts.Peek().CreatedAt < cutoff || _recentOwnOutputTexts.Count > 80))
        {
            _recentOwnOutputTexts.Dequeue();
        }
    }

    private bool IsSelfOcrSuspected(
        string ocrText,
        out RecentOwnOutputText? matchedEntry,
        out double similarity,
        out string reason)
    {
        matchedEntry = null;
        similarity = 0;
        reason = string.Empty;

        var normalizedOcr = NormalizeSelfOcrText(ocrText);
        if (normalizedOcr.Length < 3)
        {
            return false;
        }

        foreach (var entry in _recentOwnOutputTexts)
        {
            if (entry.NormalizedText.Length < 3)
            {
                continue;
            }

            var minLength = Math.Min(normalizedOcr.Length, entry.NormalizedText.Length);
            var maxLength = Math.Max(normalizedOcr.Length, entry.NormalizedText.Length);
            var containsMatch = minLength >= 8
                && minLength / (double)Math.Max(1, maxLength) >= 0.75
                && (normalizedOcr.Contains(entry.NormalizedText, StringComparison.Ordinal)
                    || entry.NormalizedText.Contains(normalizedOcr, StringComparison.Ordinal));
            var score = containsMatch
                ? 1D
                : TextSimilarity.NormalizedSimilarity(normalizedOcr, entry.NormalizedText);

            if (score < 0.88)
            {
                continue;
            }

            matchedEntry = entry;
            similarity = score;
            reason = containsMatch ? "contains_match_strict" : "similarity_threshold";
            return true;
        }

        return false;
    }

    private void LogSelfOcrSkip(
        string rawLine,
        RecentOwnOutputText? matchedEntry,
        double similarity,
        string reason)
    {
        AppLogService.AppendVerboseText(
            "self-ocr-debug.log",
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} skipped_reason=self_ocr_suspected raw_ocr_line=\"{CleanOutputLog(rawLine)}\" matched_kind=\"{CleanOutputLog(matchedEntry?.Kind ?? "<none>")}\" matched_text=\"{CleanOutputLog(matchedEntry?.RawText ?? string.Empty)}\" similarity={similarity:0.000} match_reason=\"{CleanOutputLog(reason)}\"{Environment.NewLine}");
    }

    private bool IsRealPlayerChatLine(string line)
    {
        var parsed = ChatDeduper.ParseChatLine(line);
        if (!parsed.MatchedPlayerChatPattern)
        {
            return false;
        }

        var validation = ChatDeduper.IsValidPlayerChat(parsed, _channelAliasService);
        return validation.Valid
            && !string.IsNullOrWhiteSpace(parsed.Channel)
            && !string.IsNullOrWhiteSpace(parsed.Sender)
            && !string.IsNullOrWhiteSpace(parsed.Champion)
            && !string.IsNullOrWhiteSpace(parsed.Message);
    }

    private static string NormalizeSelfOcrText(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch) || ch is >= '\u4e00' and <= '\u9fff')
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private async Task TranslateOverlayInputAsync(string text)
    {
        var target = ResolveOverlayInputTarget();
        var targetSourceLang = ChatLanguageDetector.ToSourceLangCode(target.Language);
        string? matchedConceptId = null;
        string? fallbackReason = target.FallbackReason;
        string outputText;

        try
        {
            if (ChatIntentMatcher.Match(text, targetSourceLang) is { } intentMatch)
            {
                matchedConceptId = intentMatch.ConceptId;
                outputText = intentMatch.OutputText;
            }
            else
            {
                fallbackReason = string.IsNullOrWhiteSpace(fallbackReason)
                    ? "intent_not_matched"
                    : $"{fallbackReason};intent_not_matched";
                outputText = await TranslateTextAsync(text, target.Language);
            }

            Clipboard.SetText(outputText);
            SetStatus(FormatT(
                "StatusOverlayInputTranslated",
                LocalizationService.LocalizedLanguageName(_config.UiLanguage, target.Language)));
            LogFloatingInput(text, target.LastSourceLang, matchedConceptId, targetSourceLang, outputText, fallbackReason);
        }
        catch (Exception ex)
        {
            LogFloatingInput(text, target.LastSourceLang, matchedConceptId, targetSourceLang, string.Empty, $"error:{ex.Message}");
            SetStatus(FormatT("StatusOverlayInputFailed", ex.Message));
        }
    }

    private OverlayInputTarget ResolveOverlayInputTarget()
    {
        var configured = _config.TranslateConfig.OverlayInputTargetLanguage;
        if (!string.Equals(configured, ChatLanguageDetector.AutoReverse, StringComparison.OrdinalIgnoreCase))
        {
            var manualLanguage = TranslatorLanguage.NormalizeTargetLanguage(configured);
            return new OverlayInputTarget(manualLanguage, "manual", "manual_selection", string.Empty);
        }

        if (_lastTranslationEvent is not null)
        {
            if (_lastTranslationEvent.SourceLangConfidence >= 0.65)
            {
                return new OverlayInputTarget(
                    ChatLanguageDetector.ToTranslatorLanguage(_lastTranslationEvent.SourceLang),
                    "recent_chat",
                    string.Empty,
                    _lastTranslationEvent.SourceLang);
            }

            return new OverlayInputTarget(
                ResolveOverlayInputFallbackTargetLanguage(),
                "default",
                $"low_confidence:{_lastTranslationEvent.SourceLangConfidence:0.00}",
                _lastTranslationEvent.SourceLang);
        }

        return new OverlayInputTarget(
            ResolveOverlayInputFallbackTargetLanguage(),
            "default",
            "no_last_translation_event",
            string.Empty);
    }

    private string ResolveOverlayInputFallbackTargetLanguage()
    {
        return TranslatorLanguage.NormalizeTargetLanguage(_config.TranslateConfig.OverlayInputDefaultTargetLanguage);
    }

    private void UpdateOverlayInputTargetStatus()
    {
        if (_overlayWindow is null)
        {
            return;
        }

        var target = ResolveOverlayInputTarget();
        var targetDisplay = LocalizationService.LocalizedLanguageName(_config.UiLanguage, target.Language);
        var sourceDisplay = target.SourceKind switch
        {
            "recent_chat" => LocalizationService.T(_config.UiLanguage, "RecentChatSource"),
            "manual" => LocalizationService.T(_config.UiLanguage, "ManualSelectionSource"),
            _ => LocalizationService.T(_config.UiLanguage, "DefaultSource")
        };

        _overlayWindow.SetInputTargetStatus(targetDisplay, sourceDisplay);
    }

    private async Task EnsureSelfPlayerNameAsync()
    {
        if (!string.IsNullOrWhiteSpace(_selfPlayerName))
        {
            return;
        }

        _selfPlayerName = await _liveClientDataService.GetActivePlayerNameAsync();
    }

    private void RememberLastTranslationEvent(CleanedChatMessage message, string sourceText, string translatedText)
    {
        if (string.IsNullOrWhiteSpace(sourceText)
            || string.IsNullOrWhiteSpace(translatedText)
            || TranslatorErrorSanitizer.IsErrorResult(translatedText))
        {
            return;
        }

        var sender = message.FixedPlayerName ?? message.OcrPlayerName;
        if (IsSelfSender(sender))
        {
            return;
        }

        var detection = ChatLanguageDetector.Detect(sourceText);
        _lastTranslationEvent = new LastTranslationEvent(
            sourceText,
            translatedText,
            detection.SourceLang,
            detection.Confidence,
            sender,
            message.Timestamp,
            DateTimeOffset.Now);

        UpdateOverlayInputTargetStatus();
    }

    private bool IsSelfSender(string? sender)
    {
        if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(_selfPlayerName))
        {
            return false;
        }

        return NormalizePlayerName(sender).Equals(NormalizePlayerName(_selfPlayerName), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePlayerName(string value)
    {
        var withoutTag = value.Split('#')[0];
        return new string(withoutTag.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private void LogFloatingInput(
        string inputText,
        string lastSourceLang,
        string? matchedConceptId,
        string targetLang,
        string outputText,
        string? fallbackReason)
    {
        var line = $"floating_input_text={CleanLog(inputText)} last_source_lang={CleanLog(lastSourceLang)} matched_concept_id={matchedConceptId ?? "<none>"} target_lang={targetLang} output_text={CleanLog(outputText)} fallback_reason={fallbackReason ?? "<none>"}";
        AddRealtimeDebugLog(line);

        try
        {
            AppLogService.AppendVerboseText(
                "floating-input-debug.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch
        {
            // Floating input logging should never interrupt translation.
        }
    }

    private static string CleanLog(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "<none>"
            : value.ReplaceLineEndings(" ").Trim();
    }

    private static string FormatBool(bool value)
    {
        return value ? "true" : "false";
    }

    private static string FormatRectForLog(Rect? rect)
    {
        return rect is null
            ? "<none>"
            : $"x={rect.Value.X:0},y={rect.Value.Y:0},w={rect.Value.Width:0},h={rect.Value.Height:0}";
    }

    private static string FormatMs(long? value)
    {
        return value.HasValue ? $"{value.Value}ms" : "<unknown>";
    }

    private void OpenSettingsWindow()
    {
        if (_settingsWindow is { IsVisible: true } existingWindow)
        {
            existingWindow.Activate();
            return;
        }

        var window = new SettingsWindow(_config, _configService)
        {
            ShowInTaskbar = true
        };
        _settingsWindow = window;
        window.ConfigPreviewChanged += previewConfig => _overlayWindow.ApplyConfig(previewConfig);
        window.ConfigSaved += (_, _) => ReloadConfigAfterSettingsSaved();
        window.OcrDependenciesInstalled += (_, _) =>
        {
            MarkOcrEnvironmentSetupCompleted("settings_install");
            ScheduleOcrWarmUp("ocr_dependencies_installed", delayMs: 250);
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, window))
            {
                _settingsWindow = null;
            }

            _overlayWindow.ApplyConfig(_config);
            UpdateOverlayVisibilityButton();
        };

        window.Show();
    }

    private void ReloadConfigAfterSettingsSaved()
    {
        var previousOcrEngine = OcrEngines.Normalize(_config.OcrConfig.OcrEngine);
        var previousOcrMode = OcrMode.Normalize(_config.OcrConfig.OcrMode);
        var previousOcrLanguage = OcrLanguages.Normalize(_config.OcrConfig.OcrLanguage);
        _config = _configService.Load();
        AppLogService.EnableVerboseDiagnostics = _config.EnableVerboseDiagnostics;
        _translateService.UpdateConfig(_config);
        _overlayWindow.ApplyConfig(_config);
        ApplyConfigToUi();
        UpdateOverlayVisibilityButton();
        UpdateOverlayInputTargetStatus();
        UpdateOcrEnvironmentSetupButtonVisibility();
        RegisterHotkeys();
        _lastAutoCaptureHash = null;

        if (!previousOcrEngine.Equals(OcrEngines.Normalize(_config.OcrConfig.OcrEngine), StringComparison.OrdinalIgnoreCase)
            || !previousOcrMode.Equals(OcrMode.Normalize(_config.OcrConfig.OcrMode), StringComparison.OrdinalIgnoreCase)
            || !previousOcrLanguage.Equals(OcrLanguages.Normalize(_config.OcrConfig.OcrLanguage), StringComparison.OrdinalIgnoreCase))
        {
            ScheduleOcrWarmUp("settings_changed", delayMs: 250);
        }

        SetStatus(T("StatusSettingsReloaded"));
    }

    private void ScheduleOcrWarmUp(string reason, int delayMs = 0)
    {
        _ = Task.Run(async () =>
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs);
            }

            await WarmUpOcrEngineAsync(reason);
        });
    }

    private async Task WarmUpOcrEngineAsync(string reason)
    {
        try
        {
            if (_isOcrDependencyInstallRunning)
            {
                AddRealtimeDebugLog($"ocr_warmup skipped reason={reason} dependency_install_running=true");
                return;
            }

            if (_isAutoTranslating)
            {
                AddRealtimeDebugLog($"ocr_warmup skipped reason={reason} auto_running=true");
                return;
            }

            if (Volatile.Read(ref _autoOcrInFlight) != 0)
            {
                AddRealtimeDebugLog($"ocr_warmup skipped reason={reason} auto_ocr_in_flight=true");
                return;
            }

            if (Interlocked.CompareExchange(ref _ocrWarmUpInFlight, 1, 0) != 0)
            {
                AddRealtimeDebugLog($"ocr_warmup skipped reason={reason} warmup_in_flight=true");
                return;
            }

            OcrWarmUpResult result;
            try
            {
                result = await _ocrService.WarmUpAsync(_config);
            }
            finally
            {
                Interlocked.Exchange(ref _ocrWarmUpInFlight, 0);
            }

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                AddRealtimeDebugLog($"ocr_warmup_failed reason={reason} engine={result.EngineName} mode={result.Mode} error=\"{CleanLog(result.ErrorMessage)}\"");
                return;
            }

            AddRealtimeDebugLog(
                $"ocr_warmup reason={reason} engine={result.EngineName} mode={result.Mode} cold_start_ms={FormatMs(result.ColdStartMs)} warm_run_ms={FormatMs(result.WarmRunMs)} backend=\"{CleanLog(result.Backend)}\" params=\"{CleanLog(result.Parameters)}\"");
            if (OcrEngines.Normalize(_config.OcrConfig.OcrEngine).Equals(OcrEngines.PpOcrV5Multilingual, StringComparison.OrdinalIgnoreCase))
            {
                MarkOcrEnvironmentSetupCompleted("warmup");
            }
        }
        catch (Exception ex)
        {
            AddRealtimeDebugLog($"ocr_warmup_failed reason={reason} error=\"{CleanLog(ex.Message)}\"");
        }
    }

    private void ApplyConfigToUi()
    {
        ApplyLocalization();

        var replyTargetLanguage = TranslatorLanguage.IsAnyChinese(_config.TranslateConfig.TargetLanguage)
            ? "en"
            : _config.TranslateConfig.TargetLanguage;

        SelectComboBoxByTag(ReplyTargetLanguageComboBox, replyTargetLanguage);
    }

    private void ApplyLocalization()
    {
        var language = LocalizationService.NormalizeLanguage(_config.UiLanguage);
        Title = "LOL Chat OCR Translator";
        AppSubtitleTextBlock.Text = LocalizationService.T(language, "AppSubtitle");
        InstallOcrDependenciesMainButton.Content = LocalizationService.T(language, "OcrEnvironmentSetupButton");
        StartAutoButton.Content = LocalizationService.T(language, "StartAuto");
        StopAutoButton.Content = LocalizationService.T(language, "StopAuto");
        ManualRecognizeButton.Content = LocalizationService.T(language, "ManualOnce");
        RestartTranslateButton.Content = LocalizationService.T(language, "Restart");
        ReselectRegionButton.Content = LocalizationService.T(language, "ReselectRegion");
        ViewCurrentRegionButton.Content = LocalizationService.T(language, "ViewCurrentRegion");
        TestOcrButton.Content = LocalizationService.T(language, "TestOcr");
        SettingsButton.Content = LocalizationService.T(language, "Settings");
        StatusHeaderTextBlock.Text = LocalizationService.T(language, "Status");
        RealtimeOcrHeaderTextBlock.Text = LocalizationService.T(language, "RealtimeOcrStatus");
        ReplyHeaderTextBlock.Text = LocalizationService.T(language, "ReplyTitle");
        ReplyNoticeTextBlock.Text = LocalizationService.T(language, "ReplyNotice");
        ReplyTargetLanguageTextBlock.Text = LocalizationService.T(language, "TargetLanguage");
        ReplyOutputPreviewTextBlock.Text = LocalizationService.T(language, "OutputPreview");
        TranslateReplyButton.Content = LocalizationService.T(language, "TranslateCopy");
        LocalizeLanguageComboBox(ReplyTargetLanguageComboBox, language);
        UpdateOverlayVisibilityButton();
        UpdateOverlayInputTargetStatus();
        RenderRealtimeOcrDebug();
    }

    private void RegisterHotkeys()
    {
        var issues = _hotkeyService.RegisterWindowHotkeys(
            this,
            _config,
            RunManualTranslateOnceAsync,
            ToggleAutoTranslate,
            TranslateClipboardTextAndCopyAsync,
            OpenSettingsWindow,
            ReselectRegion,
            PreviewCurrentRegion,
            ToggleOverlayVisibility,
            FocusOverlayInput);
        if (issues.Count == 0)
        {
            return;
        }

        var details = string.Join("; ", issues.Select(issue =>
            $"{issue.GestureText}: {issue.Reason}{(issue.ErrorCode is null ? string.Empty : $" Win32={issue.ErrorCode}")}"));
        AddRealtimeDebugLog($"hotkey registration failed: {details}");
        AppLogService.AppendText(
            "hotkeys.log",
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 快捷键注册失败/被占用：{details}{Environment.NewLine}");
    }

    private void UpdateAutoButtons()
    {
        StartAutoButton.IsEnabled = !_isAutoTranslating;
        StopAutoButton.IsEnabled = _isAutoTranslating;
        if (InstallOcrDependenciesMainButton.Visibility == Visibility.Visible)
        {
            InstallOcrDependenciesMainButton.IsEnabled = !_isOcrDependencyInstallRunning;
        }
    }

    private void ToggleOverlayVisibility()
    {
        if (_overlayWindow.IsVisible)
        {
            _overlayWindow.Hide();
        }
        else
        {
            _overlayWindow.ApplyConfig(_config);
            _overlayWindow.Show();
        }

        UpdateOverlayVisibilityButton();
    }

    private void FocusOverlayInput()
    {
        if (!_config.TranslateConfig.EnableOverlayInput)
        {
            _config.TranslateConfig.EnableOverlayInput = true;
            _translateService.UpdateConfig(_config);
            _configService.Save(_config);
        }

        _overlayWindow.ApplyConfig(_config);
        if (!_overlayWindow.IsVisible)
        {
            _overlayWindow.Show();
            UpdateOverlayVisibilityButton();
        }

        _overlayWindow.FocusOverlayInput();
    }

    private void UpdateOverlayVisibilityButton()
    {
        if (ToggleOverlayVisibilityButton is null)
        {
            return;
        }

        ToggleOverlayVisibilityButton.Content = _overlayWindow.IsVisible
            ? T("HideOverlay")
            : T("ShowOverlay");
    }

    private void ReselectRegion()
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
            AppLogService.AppendText(
                "app-errors.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} region_selector_create_failed {ex}{Environment.NewLine}");
            SetStatus($"框选窗口打开失败：{ex.Message}");
            return;
        }

        bool? dialogResult;
        try
        {
            dialogResult = selector.ShowDialog();
        }
        catch (Exception ex)
        {
            AppLogService.AppendText(
                "app-errors.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} region_selector_show_failed {ex}{Environment.NewLine}");
            SetStatus($"框选窗口显示失败：{ex.Message}");
            return;
        }

        if (dialogResult == true && selector.SelectedRegion is { } region)
        {
            _config.OcrConfig.RegionX = region.X;
            _config.OcrConfig.RegionY = region.Y;
            _config.OcrConfig.RegionWidth = region.Width;
            _config.OcrConfig.RegionHeight = region.Height;
            _configService.Save(_config);
            _lastAutoCaptureHash = null;

            try
            {
                var debugResult = SaveCaptureDebugImages(selector.SelectionDebugInfo);
                SetStatus($"{FormatT("StatusRegionUpdated", region.X, region.Y, region.Width, region.Height)} {FormatT("StatusCaptureDebugSaved", debugResult.DirectoryPath)}");
            }
            catch (Exception ex)
            {
                SetStatus($"{FormatT("StatusRegionUpdated", region.X, region.Y, region.Width, region.Height)} 框选调试图保存失败：{ex.Message}");
            }
        }
    }

    private void PreviewCurrentRegion()
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

    private OcrCaptureDebugResult SaveCaptureDebugImages(OcrSelectionDebugInfo? selectionDebugInfo)
    {
        return OcrCaptureDebugService.SaveDebugImages(_config.OcrConfig, selectionDebugInfo);
    }

    private List<CleanedChatMessage> GetStableMessages(
        List<CleanedChatMessage> messages,
        bool forceFlushPending,
        string source)
    {
        var allowImmediateLongMessages = forceFlushPending
            && !source.Equals("auto-start", StringComparison.OrdinalIgnoreCase)
            && !source.Equals("pending-stability", StringComparison.OrdinalIgnoreCase);
        var readyMessages = _pendingMessageStabilizer.GetStableMessages(
            messages,
            forceFlushPending,
            allowImmediateLongMessages,
            source,
            _config.OcrConfig.CaptureIntervalMs);
        return DeduplicateStableMessages(readyMessages);
    }

    private string BuildOriginalDisplayText(CleanedChatMessage message)
    {
        var playerName = message.FixedPlayerName ?? message.OcrPlayerName;
        var championName = message.FixedChampionName ?? message.OcrChampionText;
        var channelPrefix = _config.FilterConfig.RemoveChannelTag
            ? string.Empty
            : $"[{GetChannelDisplayName(message.Channel)}] ";

        if (!_config.OverlayConfig.ShowSenderName)
        {
            return $"{channelPrefix}{T("ChatLabel")}";
        }

        if (!string.IsNullOrWhiteSpace(playerName) && !string.IsNullOrWhiteSpace(championName))
        {
            return $"{channelPrefix}{playerName}（{championName}）";
        }

        if (!string.IsNullOrWhiteSpace(playerName))
        {
            return $"{channelPrefix}{playerName}";
        }

        return $"{channelPrefix}{T("ChatLabel")}";
    }

    private async Task<Guid> AddOverlayMessageAsync(string originalText, string translatedText, ChatChannel channel)
    {
        if (Dispatcher.CheckAccess())
        {
            return _overlayWindow.AddMessage(originalText, translatedText, channel);
        }

        return await Dispatcher.InvokeAsync(
            () => _overlayWindow.AddMessage(originalText, translatedText, channel),
            DispatcherPriority.Send);
    }

    private async Task<IReadOnlyList<Guid>> AddOverlayMessagesAsync(IReadOnlyList<OverlayMessageRequest> messages)
    {
        if (messages.Count == 0)
        {
            return [];
        }

        if (Dispatcher.CheckAccess())
        {
            return _overlayWindow.AddMessagesInOrder(messages);
        }

        return await Dispatcher.InvokeAsync(
            () => _overlayWindow.AddMessagesInOrder(messages),
            DispatcherPriority.Send);
    }

    private async Task UpdateOverlayTranslationAsync(Guid overlayId, string translatedText)
    {
        if (Dispatcher.CheckAccess())
        {
            _overlayWindow.UpdateTranslation(overlayId, translatedText);
            return;
        }

        await Dispatcher.InvokeAsync(
            () => _overlayWindow.UpdateTranslation(overlayId, translatedText),
            DispatcherPriority.Send);
    }

    private string GetChannelDisplayName(ChatChannel channel)
    {
        return channel switch
        {
            ChatChannel.Team => T("ChannelTeam"),
            ChatChannel.All => T("ChannelAll"),
            ChatChannel.Party => T("ChannelParty"),
            ChatChannel.System => T("ChannelSystem"),
            _ => T("ChannelUnknown")
        };
    }

    private static string BuildOcrStatusMessage(
        string language,
        OcrRecognitionResult ocrResult,
        List<CleanedChatMessage> cleanedMessages,
        int stableMessageCount,
        int currentPlayerCount,
        int translatedCount,
        int duplicateCount)
    {
        static string Text(string language, string key) => LocalizationService.T(language, key);
        static string Format(string language, string key, params object[] args) =>
            string.Format(Text(language, key), args);

        if (translatedCount > 0)
        {
            return Format(language, "OcrStatusTranslated", translatedCount, ocrResult.Lines.Count, cleanedMessages.Count, currentPlayerCount);
        }

        if (!ocrResult.CaptureSucceeded)
        {
            return ocrResult.ErrorMessage ?? Text(language, "StatusCaptureFailedReselectRegion");
        }

        if (!string.IsNullOrWhiteSpace(ocrResult.ErrorMessage) && ocrResult.Lines.Count == 0)
        {
            return Format(language, "OcrStatusNoTextWithError", ocrResult.EngineName, ocrResult.ErrorMessage);
        }

        if (ocrResult.Lines.Count == 0)
        {
            return Format(language, "OcrStatusNoText", ocrResult.EngineName);
        }

        if (cleanedMessages.Count == 0)
        {
            return Format(language, "OcrStatusNoCleanedMessages", ocrResult.Lines.Count, BuildLineSample(language, ocrResult.Lines));
        }

        if (stableMessageCount == 0)
        {
            return Format(language, "OcrStatusWaitingStableText", ocrResult.Lines.Count, cleanedMessages.Count);
        }

        if (duplicateCount == stableMessageCount)
        {
            return Format(language, "OcrStatusAllDuplicate", ocrResult.Lines.Count, cleanedMessages.Count);
        }

        return Format(language, "OcrStatusNoNewTranslation", ocrResult.Lines.Count, cleanedMessages.Count);
    }

    private static string BuildLineSample(string language, IEnumerable<string> lines)
    {
        var sample = string.Join(" / ", lines.Take(3));
        return string.IsNullOrWhiteSpace(sample) ? LocalizationService.T(language, "NoneValue") : sample;
    }

    private static void LogOcrCycle(
        OcrRecognitionResult ocrResult,
        List<CleanedChatMessage> cleanedMessages,
        int stableMessageCount,
        int currentPlayerCount,
        int translatedCount,
        int duplicateCount)
    {
        try
        {
            var rawLines = ocrResult.Lines.Count == 0
                ? "  <none>"
                : string.Join(Environment.NewLine, ocrResult.Lines.Take(20).Select(line => $"  {line}"));
            var cleaned = cleanedMessages.Count == 0
                ? "  <none>"
                : string.Join(Environment.NewLine, cleanedMessages.Take(20).Select(message =>
                    $"  timestamp={message.Timestamp ?? "<none>"} channel={message.Channel} rawChannel={message.RawChannelText ?? "<none>"} player={message.OcrPlayerName ?? "<none>"} champion={message.OcrChampionText ?? "<none>"} fixedPlayer={message.FixedPlayerName ?? "<none>"} fixedChampion={message.FixedChampionName ?? "<none>"} message={message.Message}"));

            AppLogService.AppendVerboseText(
                "ocr-debug.log",
                $"""
                [{DateTime.Now:yyyy-MM-dd HH:mm:ss}]
                Engine: {ocrResult.EngineName}
                Diagnostics: {FormatOcrDiagnosticsForLog(ocrResult.Diagnostics)}
                CaptureSucceeded: {ocrResult.CaptureSucceeded}
                Error: {ocrResult.ErrorMessage ?? "<none>"}
                RawCount: {ocrResult.Lines.Count}
                CleanedCount: {cleanedMessages.Count}
                StableCount: {stableMessageCount}
                CurrentPlayerCount: {currentPlayerCount}
                DuplicateCount: {duplicateCount}
                TranslatedCount: {translatedCount}
                RawLines:
                {rawLines}
                CleanedLines:
                {cleaned}

                """);
        }
        catch
        {
            // Debug logging should never interrupt OCR/translation flow.
        }
    }

    private static string FormatOcrDiagnosticsForLog(OcrRunDiagnostics? diagnostics)
    {
        if (diagnostics is null)
        {
            return "capture_ms=<unknown> text_mask_ms=<unknown> dirty_detect_ms=<unknown> crop_ms=<unknown> preprocess_ms=<unknown> ocr_full_ms=<unknown> ocr_recognize_lines_ms=<unknown> ocr_total_ms=<unknown> translate_ms=<unknown> cycle_total_ms=<unknown> backend=unknown mode=unknown ocr_input_image_path=<unknown>";
        }

        static string Ms(long? value) => value.HasValue ? $"{value.Value}ms" : "<unknown>";
        static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? "<none>" : value.ReplaceLineEndings(" ").Trim();

        return string.Join(
            " ",
            [
                $"capture_ms={Ms(diagnostics.CaptureMs)}",
                $"text_mask_ms={Ms(diagnostics.TextMaskMs)}",
                $"dirty_detect_ms={Ms(diagnostics.DirtyDetectMs)}",
                $"crop_ms={Ms(diagnostics.CropMs)}",
                $"preprocess_ms={Ms(diagnostics.PreprocessMs)}",
                $"ocr_detect_ms={Ms(diagnostics.OcrDetectMs)}",
                $"ocr_recognize_ms={Ms(diagnostics.OcrRecognizeMs)}",
                $"ocr_full_ms={Ms(diagnostics.OcrFullMs)}",
                $"ocr_recognize_lines_ms={Ms(diagnostics.OcrRecognizeLinesMs)}",
                $"ocr_total_ms={Ms(diagnostics.OcrTotalMs)}",
                $"ocr_request_ms={Ms(diagnostics.OcrRequestMs)}",
                $"ocr_inference_ms={Ms(diagnostics.OcrInferenceMs)}",
                $"json_parse_ms={Ms(diagnostics.JsonParseMs)}",
                $"postprocess_ms={Ms(diagnostics.PostProcessMs)}",
                $"dedupe_ms={Ms(diagnostics.DedupeMs)}",
                $"translate_ms={Ms(diagnostics.TranslateMs)}",
                $"overlay_ms={Ms(diagnostics.OverlayMs)}",
                $"cycle_total_ms={Ms(diagnostics.CycleTotalMs)}",
                $"total_ms={Ms(diagnostics.TotalMs)}",
                $"cold_start_ms={Ms(diagnostics.ColdStartMs)}",
                $"worker_start_ms={Ms(diagnostics.WorkerStartMs)}",
                $"warm_run_ms={Ms(diagnostics.WarmRunMs)}",
                $"model_init_ms={Ms(diagnostics.ModelInitMs)}",
                $"backend=\"{Clean(diagnostics.Backend)}\"",
                $"mode={Clean(diagnostics.Mode)}",
                $"task={Clean(diagnostics.Task)}",
                $"performance_mode={Clean(diagnostics.PerformanceMode)}",
                $"selected_lang={Clean(diagnostics.SelectedLanguage)}",
                $"paddleocr_version={Clean(diagnostics.PaddleOcrVersion)}",
                $"paddlepaddle_version={Clean(diagnostics.PaddlePaddleVersion)}",
                $"det_model=\"{Clean(diagnostics.DetModelName)}\"",
                $"rec_model=\"{Clean(diagnostics.RecModelName)}\"",
                $"use_space_char={diagnostics.UseSpaceChar?.ToString().ToLowerInvariant() ?? "<unknown>"}",
                $"fallback_reason=\"{Clean(diagnostics.FallbackReason)}\"",
                $"unsupported_parameters=\"{Clean(diagnostics.UnsupportedParameters)}\"",
                $"worker_cold_start={diagnostics.WorkerColdStart?.ToString().ToLowerInvariant() ?? "<unknown>"}",
                $"model_already_loaded={diagnostics.ModelAlreadyLoaded?.ToString().ToLowerInvariant() ?? "<unknown>"}",
                $"worker_exit_code={diagnostics.WorkerExitCode?.ToString() ?? "<none>"}",
                $"worker_stderr_tail=\"{Clean(diagnostics.WorkerStderrTail)}\"",
                $"worker_stdout_tail=\"{Clean(diagnostics.WorkerStdoutTail)}\"",
                $"worker_log_path=\"{Clean(diagnostics.WorkerLogPath)}\"",
                $"worker_script_path=\"{Clean(diagnostics.WorkerScriptPath)}\"",
                $"worker_script_last_write_time=\"{Clean(diagnostics.WorkerScriptLastWriteTime)}\"",
                $"worker_script_sha256={Clean(diagnostics.WorkerScriptSha256)}",
                $"source_script_path=\"{Clean(diagnostics.SourceScriptPath)}\"",
                $"source_script_sha256={Clean(diagnostics.SourceScriptSha256)}",
                $"last_request_id={Clean(diagnostics.LastRequestId)}",
                $"last_request_action={Clean(diagnostics.LastRequestAction)}",
                $"last_request_image_path=\"{Clean(diagnostics.LastRequestImagePath)}\"",
                $"last_request_mode={Clean(diagnostics.LastRequestMode)}",
                $"last_request_lang={Clean(diagnostics.LastRequestLang)}",
                $"last_request_task={Clean(diagnostics.LastRequestTask)}",
                $"payload_error_kind={Clean(diagnostics.PayloadErrorKind)}",
                $"restart_worker={diagnostics.RestartWorker?.ToString().ToLowerInvariant() ?? "<unknown>"}",
                $"used_full_ocr={diagnostics.UsedFullOcr?.ToString().ToLowerInvariant() ?? "<unknown>"}",
                $"used_recognition_only={diagnostics.UsedRecognitionOnly?.ToString().ToLowerInvariant() ?? "<unknown>"}",
                $"local_region_full_ocr={diagnostics.LocalRegionFullOcr?.ToString().ToLowerInvariant() ?? "<unknown>"}",
                $"recognition_only_reason=\"{Clean(diagnostics.RecognitionOnlyReason)}\"",
                $"dirty_line_count={diagnostics.DirtyLineCount?.ToString() ?? "<unknown>"}",
                $"changed_pixels={diagnostics.ChangedPixels?.ToString() ?? "<unknown>"}",
                $"dirty_region_ratio={diagnostics.DirtyRegionRatio?.ToString("0.###") ?? "<unknown>"}",
                $"full_rescan_reason=\"{Clean(diagnostics.FullRescanReason)}\"",
                $"crop_regions=\"{Clean(diagnostics.CropRegions)}\"",
                $"text_mask_changed={diagnostics.TextMaskChanged?.ToString().ToLowerInvariant() ?? "<unknown>"}",
                $"same_capture={diagnostics.SameAsLastCapture}",
                $"capture_hash={Clean(diagnostics.CaptureHash)}",
                $"ocr_input_image_path=\"{Clean(diagnostics.OcrInputImagePath)}\"",
                $"reading_order={Clean(diagnostics.ReadingOrder)}",
                $"raw_text=\"{Clean(diagnostics.RawText)}\"",
                $"params=\"{Clean(diagnostics.Parameters)}\""
            ]);
    }

    private static string FormatDirtyRegions(IReadOnlyList<System.Drawing.Rectangle>? regions)
    {
        if (regions is null || regions.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            "|",
            regions.Select(region => $"{region.X},{region.Y},{region.Width},{region.Height}"));
    }

    private static OcrRunDiagnostics EnrichOcrDiagnostics(
        OcrRunDiagnostics? diagnostics,
        long captureMs,
        long cropMs,
        long? totalMs = null,
        long? maskMs = null,
        int? dirtyLineCount = null,
        string? fullRescanReason = null,
        bool? usedFullOcr = null,
        bool? usedRecognitionOnly = null,
        string? recognitionOnlyReason = null,
        bool? localRegionFullOcr = null,
        int? changedPixels = null,
        double? dirtyRegionRatio = null,
        string? cropRegions = null,
        bool? textMaskChanged = null,
        long? translateMs = null,
        long? postProcessMs = null,
        long? dedupeMs = null,
        long? overlayMs = null,
        long? cycleTotalMs = null)
    {
        diagnostics ??= new OcrRunDiagnostics();
        return diagnostics with
        {
            CaptureMs = diagnostics.CaptureMs ?? (captureMs > 0 ? captureMs : null),
            CropMs = diagnostics.CropMs ?? cropMs,
            TextMaskMs = diagnostics.TextMaskMs ?? maskMs,
            DirtyDetectMs = diagnostics.DirtyDetectMs ?? maskMs,
            DirtyLineCount = diagnostics.DirtyLineCount ?? dirtyLineCount,
            FullRescanReason = diagnostics.FullRescanReason ?? fullRescanReason,
            UsedFullOcr = diagnostics.UsedFullOcr ?? usedFullOcr,
            UsedRecognitionOnly = diagnostics.UsedRecognitionOnly ?? usedRecognitionOnly,
            RecognitionOnlyReason = diagnostics.RecognitionOnlyReason ?? recognitionOnlyReason,
            LocalRegionFullOcr = diagnostics.LocalRegionFullOcr ?? localRegionFullOcr,
            ChangedPixels = diagnostics.ChangedPixels ?? changedPixels,
            DirtyRegionRatio = diagnostics.DirtyRegionRatio ?? dirtyRegionRatio,
            CropRegions = diagnostics.CropRegions ?? cropRegions,
            TextMaskChanged = diagnostics.TextMaskChanged ?? textMaskChanged,
            TranslateMs = diagnostics.TranslateMs ?? translateMs,
            PostProcessMs = diagnostics.PostProcessMs ?? postProcessMs,
            DedupeMs = diagnostics.DedupeMs ?? dedupeMs,
            OverlayMs = diagnostics.OverlayMs ?? overlayMs,
            CycleTotalMs = diagnostics.CycleTotalMs ?? cycleTotalMs,
            TotalMs = totalMs ?? diagnostics.TotalMs
        };
    }

    private static bool TryGetOcrSkipReason(OcrRecognitionResult result, out string reason)
    {
        reason = string.Empty;
        var parameters = result.Diagnostics?.Parameters ?? string.Empty;
        foreach (var parameter in parameters.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!parameter.StartsWith("reason=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            reason = parameter["reason=".Length..].Trim();
            return reason is "ocr_busy" or "ocr_timeout" or "ocr_cold_start_timeout";
        }

        if (!string.IsNullOrWhiteSpace(result.Diagnostics?.PayloadErrorKind)
            && result.Lines.Count == 0)
        {
            reason = result.Diagnostics.PayloadErrorKind.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                ? "ocr_timeout"
                : "worker_error";
            return true;
        }

        if (result.ErrorMessage?.Contains("OCR timeout", StringComparison.OrdinalIgnoreCase) == true)
        {
            reason = "ocr_timeout";
            return true;
        }

        if (result.ErrorMessage?.Contains("OCR already running", StringComparison.OrdinalIgnoreCase) == true
            || result.ErrorMessage?.Contains("worker 正忙", StringComparison.OrdinalIgnoreCase) == true)
        {
            reason = "ocr_busy";
            return true;
        }

        return false;
    }

    private static List<CleanedChatMessage> DeduplicateStableMessages(IEnumerable<CleanedChatMessage> messages)
    {
        var deduplicated = new List<CleanedChatMessage>();

        foreach (var message in messages
                     .OrderBy(message => message.SourceOrder)
                     .ThenBy(message => message.SourceTop ?? double.MaxValue)
                     .ThenBy(message => message.SourceLeft ?? double.MaxValue)
                     .ThenBy(message => message.SourceRawLineIndex))
        {
            var existing = deduplicated.FirstOrDefault(item => HaveSameChatIdentity(item, message));
            if (existing is null)
            {
                deduplicated.Add(message);
                continue;
            }

            if (IsMoreCompleteMessage(message.Message, existing.Message))
            {
                var index = deduplicated.IndexOf(existing);
                message.SourceOrder = Math.Min(existing.SourceOrder, message.SourceOrder);
                message.SourceTop ??= existing.SourceTop;
                message.SourceLeft ??= existing.SourceLeft;
                message.SourceRawLineIndex = existing.SourceRawLineIndex >= 0
                    ? existing.SourceRawLineIndex
                    : message.SourceRawLineIndex;
                deduplicated[index] = message;
            }
        }

        return deduplicated
            .OrderBy(message => message.SourceOrder)
            .ThenBy(message => message.SourceTop ?? double.MaxValue)
            .ThenBy(message => message.SourceLeft ?? double.MaxValue)
            .ThenBy(message => message.SourceRawLineIndex)
            .ToList();
    }

    private static bool HaveSameChatIdentity(CleanedChatMessage left, CleanedChatMessage right)
    {
        var leftSender = NormalizeParticipantKey(left.FixedPlayerName ?? left.OcrPlayerName);
        var rightSender = NormalizeParticipantKey(right.FixedPlayerName ?? right.OcrPlayerName);
        var sameSender = !string.IsNullOrWhiteSpace(leftSender)
            && leftSender.Equals(rightSender, StringComparison.OrdinalIgnoreCase);
        if (sameSender)
        {
            var leftTimestamp = NormalizeStableTimestamp(left.Timestamp);
            var rightTimestamp = NormalizeStableTimestamp(right.Timestamp);
            var leftMessage = ChatDeduper.NormalizeMessage(left.Message);
            var rightMessage = ChatDeduper.NormalizeMessage(right.Message);

            if (!string.IsNullOrWhiteSpace(leftTimestamp)
                && leftTimestamp.Equals(rightTimestamp, StringComparison.OrdinalIgnoreCase)
                && AreLikelySameOcrMessage(leftMessage, rightMessage))
            {
                return true;
            }

            if ((string.IsNullOrWhiteSpace(leftTimestamp) || string.IsNullOrWhiteSpace(rightTimestamp))
                && AreLikelySameOcrMessage(leftMessage, rightMessage))
            {
                return true;
            }
        }

        var leftKey = ChatDeduper.BuildStableIdentityKey(left);
        var rightKey = ChatDeduper.BuildStableIdentityKey(right);
        return !string.IsNullOrWhiteSpace(leftKey)
            && leftKey.Equals(rightKey, StringComparison.OrdinalIgnoreCase);
    }

    private static bool AreLikelySameOcrMessage(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        if (left.Equals(right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leftCompact = NormalizeMessageKey(left);
        var rightCompact = NormalizeMessageKey(right);
        if (string.IsNullOrWhiteSpace(leftCompact) || string.IsNullOrWhiteSpace(rightCompact))
        {
            return false;
        }

        if (leftCompact.Equals(rightCompact, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsStrictStablePrefix(leftCompact, rightCompact)
            || IsStrictStablePrefix(rightCompact, leftCompact))
        {
            return true;
        }

        if (IsLikelyStableSplitTokenPrefix(leftCompact, rightCompact)
            || IsLikelyStableSplitTokenPrefix(rightCompact, leftCompact))
        {
            return true;
        }

        var minLength = Math.Min(leftCompact.Length, rightCompact.Length);
        var maxLength = Math.Max(leftCompact.Length, rightCompact.Length);
        if (minLength <= 2)
        {
            return maxLength <= 4
                && (leftCompact.Contains(rightCompact, StringComparison.OrdinalIgnoreCase)
                    || rightCompact.Contains(leftCompact, StringComparison.OrdinalIgnoreCase));
        }

        if (leftCompact.Contains(rightCompact, StringComparison.OrdinalIgnoreCase)
            || rightCompact.Contains(leftCompact, StringComparison.OrdinalIgnoreCase))
        {
            return minLength / (double)maxLength >= 0.72;
        }

        return TextSimilarity.NormalizedSimilarity(leftCompact, rightCompact) >= 0.86;
    }

    private static bool IsStrictStablePrefix(string possiblePrefix, string fullText)
    {
        return possiblePrefix.Length >= 24
            && fullText.Length - possiblePrefix.Length >= 8
            && fullText.StartsWith(possiblePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPartialDuplicateCandidate(string value)
    {
        return value.Length >= 24;
    }

    private static bool IsLikelyStableSplitTokenPrefix(string possiblePrefix, string fullText)
    {
        if (possiblePrefix.Length >= fullText.Length)
        {
            return false;
        }

        var commonLength = 0;
        var max = Math.Min(possiblePrefix.Length, fullText.Length);
        while (commonLength < max
            && char.ToLowerInvariant(possiblePrefix[commonLength]) == char.ToLowerInvariant(fullText[commonLength]))
        {
            commonLength++;
        }

        var unmatchedTailLength = possiblePrefix.Length - commonLength;
        return commonLength >= 24
            && unmatchedTailLength is >= 1 and <= 4
            && fullText.Length - possiblePrefix.Length >= 4;
    }

    private static string NormalizeStableTimestamp(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace('：', ':');
    }

    private static string NormalizeParticipantKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value
            .Normalize()
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray();

        return new string(chars);
    }

    private static bool IsMoreCompleteMessage(string candidate, string current)
    {
        return NormalizeMessageKey(candidate).Length > NormalizeMessageKey(current).Length;
    }

    private static string NormalizeMessageKey(string value)
    {
        var chars = value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray();

        return new string(chars);
    }

    private string T(string key)
    {
        return LocalizationService.T(_config.UiLanguage, key);
    }

    private string FormatT(string key, params object[] args)
    {
        return string.Format(T(key), args);
    }

    private bool TryBeginInvoke(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return false;
        }

        try
        {
            Dispatcher.BeginInvoke(action);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SetStatus(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            TryBeginInvoke(() => SetStatus(message));
            return;
        }

        StatusText.Text = message;
    }

    private void ShowDevelopmentNoticeOnce()
    {
        if (_config.HasShownDevelopmentNotice)
        {
            return;
        }

        _config.HasShownDevelopmentNotice = true;
        _configService.Save(_config);
        TryBeginInvoke(() =>
        {
            MessageBox.Show(
                this,
                "目前处于开发阶段，可能存在不稳定情况！",
                "LoLChatTranslator",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        });
    }

    private void ShowOcrEnvironmentSetupPromptOnce()
    {
        if (_config.HasShownOcrEnvironmentSetupPrompt || _config.HasCompletedOcrEnvironmentSetup)
        {
            return;
        }

        _config.HasShownOcrEnvironmentSetupPrompt = true;
        _configService.Save(_config);
        TryBeginInvoke(async () =>
        {
            if (_config.HasCompletedOcrEnvironmentSetup || _isOcrDependencyInstallRunning || _closeRequested)
            {
                return;
            }

            var choice = MessageBox.Show(
                this,
                T("OcrEnvironmentSetupPrompt"),
                T("OcrEnvironmentSetupPromptTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Information,
                MessageBoxResult.Yes);

            if (choice == MessageBoxResult.Yes)
            {
                await InstallOcrDependenciesFromMainAsync("startup_prompt");
            }
        });
    }

    private void MarkOcrEnvironmentSetupCompleted(string reason)
    {
        if (!Dispatcher.CheckAccess())
        {
            TryBeginInvoke(() => MarkOcrEnvironmentSetupCompleted(reason));
            return;
        }

        if (!_config.HasCompletedOcrEnvironmentSetup)
        {
            _config.HasCompletedOcrEnvironmentSetup = true;
            _configService.Save(_config);
        }

        AddRealtimeDebugLog($"ocr_environment_setup_completed reason={reason}");
        UpdateOcrEnvironmentSetupButtonVisibility();
    }

    private void UpdateOcrEnvironmentSetupButtonVisibility()
    {
        if (!Dispatcher.CheckAccess())
        {
            TryBeginInvoke(UpdateOcrEnvironmentSetupButtonVisibility);
            return;
        }

        var shouldShow = !_config.HasCompletedOcrEnvironmentSetup;
        InstallOcrDependenciesMainButton.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
        InstallOcrDependenciesMainButton.IsEnabled = shouldShow && !_isOcrDependencyInstallRunning;
    }

    private void SetRealtimeSkip(string reason)
    {
        UpdateRealtimeOcrDebug(state => state.LastSkipReason = reason);
    }

    private void AddRealtimeDebugLog(string message)
    {
        Trace.TraceInformation($"[RealtimeOCR] {message}");

        if (!Dispatcher.CheckAccess())
        {
            TryBeginInvoke(() => AddRealtimeDebugLog(message));
            return;
        }

        _realtimeDebugLines.Enqueue($"{DateTime.Now:HH:mm:ss} [RealtimeOCR] {message}");
        while (_realtimeDebugLines.Count > 10)
        {
            _realtimeDebugLines.Dequeue();
        }

        RenderRealtimeOcrDebug();
    }

    private void UpdateRealtimeOcrDebug(Action<RealtimeOcrDebugState> update)
    {
        if (!Dispatcher.CheckAccess())
        {
            TryBeginInvoke(() => UpdateRealtimeOcrDebug(update));
            return;
        }

        update(_realtimeOcrDebug);
        RenderRealtimeOcrDebug();
    }

    private void RenderRealtimeOcrDebug()
    {
        if (!Dispatcher.CheckAccess())
        {
            TryBeginInvoke(RenderRealtimeOcrDebug);
            return;
        }

        RealtimeOcrStatusTextBox.Text = string.Join(
            Environment.NewLine,
            [
                $"{T("RealtimeOcr")}：{LocalizeRealtimeValue(_realtimeOcrDebug.RunningStatus)}",
                $"{T("LastCaptureTime")}：{LocalizeRealtimeValue(_realtimeOcrDebug.LastCaptureTime)}",
                $"{T("LastTriggerReason")}：{LocalizeRealtimeValue(_realtimeOcrDebug.LastTriggerReason)}",
                $"{T("LastOcrElapsed")}：{_realtimeOcrDebug.LastOcrElapsedMs}ms",
                $"{T("LastOcrLineCount")}：{_realtimeOcrDebug.LastOcrResultLineCount}",
                $"{T("LastElapsed")}：{_realtimeOcrDebug.LastElapsedMs}ms",
                $"{T("LastTranslationStatus")}：{LocalizeRealtimeValue(_realtimeOcrDebug.LastTranslationStatus)}",
                $"{T("SkipReason")}：{LocalizeRealtimeValue(_realtimeOcrDebug.LastSkipReason)}",
                $"{T("Error")}：{LocalizeRealtimeValue(_realtimeOcrDebug.LastError)}",
                $"-- {T("Logs")} --",
                .. _realtimeDebugLines.Reverse()
            ]);
    }

    private string LocalizeRealtimeValue(string value)
    {
        return value switch
        {
            "" or "-" => T("NoneValue"),
            "stopped" => T("Stopped"),
            "running" => T("Running"),
            "recognizing" => T("StatusRecognizing"),
            "translating" => T("Translating"),
            "done" => T("Done"),
            "error" => T("ErrorStatus"),
            "empty_result" => T("EmptyResult"),
            "no_region" => T("NoRegion"),
            "mask_no_change" => T("MaskNoChange"),
            "textmask_changed" => T("TextMaskChanged"),
            "fallback" => T("FallbackScan"),
            "fixed_interval" => T("FixedIntervalScan"),
            "ocr_busy" => T("OcrBusy"),
            "ocr_timeout" => T("StatusOcrTimeoutSkipped"),
            "ocr_cold_start_timeout" => UiTextLocalizer.Text(_config.UiLanguage, "OCR 初始化超时，已重置识别引擎", "OCR 初始化逾時，已重置辨識引擎", "OCR initialization timed out; engine was reset", "OCR 초기화 시간이 초과되어 인식 엔진을 재설정했습니다", "OCR 初期化がタイムアウトしたため認識エンジンをリセットしました", "OCR khởi tạo quá thời gian; đã đặt lại bộ nhận dạng"),
            "cooldown" => T("OcrCooldown"),
            "duplicate_screenshot" => UiTextLocalizer.Text(_config.UiLanguage, "截图未变化", "截圖未變化", "Screenshot unchanged", "스크린샷 변화 없음", "スクリーンショット未変更", "Ảnh chụp không đổi"),
            "duplicate_only" => UiTextLocalizer.Text(_config.UiLanguage, "全部为重复消息", "全部為重複訊息", "Duplicate messages only", "중복 메시지만 있음", "重複メッセージのみ", "Chỉ có tin nhắn trùng"),
            "no_ocr_lines" => UiTextLocalizer.Text(_config.UiLanguage, "未识别到文字", "未辨識到文字", "No OCR lines", "OCR 텍스트 없음", "OCR 行なし", "Không có dòng OCR"),
            "no_valid_chat" => UiTextLocalizer.Text(_config.UiLanguage, "未识别到有效玩家聊天", "未辨識到有效玩家聊天", "No valid player chat", "유효한 플레이어 채팅 없음", "有効なプレイヤーチャットなし", "Không có chat hợp lệ"),
            "excluded_player_only" => UiTextLocalizer.Text(_config.UiLanguage, "仅有已排除玩家消息", "僅有已排除玩家訊息", "Excluded player messages only", "제외된 플레이어 메시지만 있음", "除外プレイヤーのメッセージのみ", "Chỉ có tin nhắn người chơi bị loại trừ"),
            "pending_unstable" => UiTextLocalizer.Text(_config.UiLanguage, "等待长句稳定", "等待長句穩定", "Waiting for stable text", "텍스트 안정 대기", "テキスト安定待ち", "Đang chờ chữ ổn định"),
            "translation_failed" => UiTextLocalizer.Text(_config.UiLanguage, "翻译失败，等待重试", "翻譯失敗，等待重試", "Translation failed, will retry", "번역 실패, 재시도 대기", "翻訳失敗、再試行待ち", "Dịch thất bại, sẽ thử lại"),
            "untranslated_output" => UiTextLocalizer.Text(_config.UiLanguage, "翻译疑似未生效，已跳过", "翻譯疑似未生效，已略過", "Untranslated output skipped", "번역 안 된 출력 건너뜀", "未翻訳出力をスキップ", "Bỏ qua kết quả chưa dịch"),
            "worker_error" => UiTextLocalizer.Text(_config.UiLanguage, "OCR worker 错误", "OCR worker 錯誤", "OCR worker error", "OCR worker 오류", "OCR worker エラー", "Lỗi OCR worker"),
            "no_new_translation" => UiTextLocalizer.Text(_config.UiLanguage, "没有新的可显示翻译", "沒有新的可顯示翻譯", "No new displayable translation", "표시할 새 번역 없음", "表示できる新しい翻訳なし", "Không có bản dịch mới để hiển thị"),
            "self_ocr_suspected" => UiTextLocalizer.Text(_config.UiLanguage, "疑似识别到本程序输出", "疑似辨識到本程式輸出", "Suspected self-OCR", "자체 출력 OCR 의심", "自己 OCR の疑い", "Nghi OCR đọc lại ứng dụng"),
            "manual" => T("ManualOnce"),
            _ => value
        };
    }

    private void AddOcrTiming(OcrTimingSample sample)
    {
        if (!Dispatcher.CheckAccess())
        {
            TryBeginInvoke(() => AddOcrTiming(sample));
            return;
        }

        _recentOcrTimings.Enqueue(sample);
        while (_recentOcrTimings.Count > 10)
        {
            _recentOcrTimings.Dequeue();
        }

        OcrTimingTextBlock.Text = string.Join(
            "  ",
            _recentOcrTimings
                .Reverse()
                .Take(5)
                .Select(item => $"ocr={item.OcrTotalMs}ms prep={item.PreprocessMs}ms det={FormatMs(item.OcrDetectMs)} rec={FormatMs(item.OcrRecognizeMs)}"));
    }

    private void RecordOcrTiming(
        long captureMs,
        long cropMs,
        long maskMs,
        long preprocessMs,
        long? ocrDetectMs,
        long? ocrRecognizeMs,
        long ocrTotalMs,
        long parseMs,
        long translateMs,
        long totalMs)
    {
        AddOcrTiming(new OcrTimingSample(
            captureMs,
            cropMs,
            maskMs,
            preprocessMs,
            ocrDetectMs,
            ocrRecognizeMs,
            ocrTotalMs,
            parseMs,
            translateMs,
            totalMs));
    }

    private string GetSelectedReplyTargetLanguage()
    {
        return (ReplyTargetLanguageComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            ?? _config.TranslateConfig.TargetLanguage;
    }

    private void AddGlossaryDebugLog(NormalizedMessage normalizedMessage)
    {
        if (!normalizedMessage.GlossaryMatched)
        {
            return;
        }

        AddRealtimeDebugLog($"glossary_match level={normalizedMessage.GlossaryMatchLevel} confidence={normalizedMessage.GlossaryConfidence:0.00}");
    }

    private void AddChatDedupeDebugLog(ChatDedupeDecision decision)
    {
        var reference = string.IsNullOrWhiteSpace(decision.DuplicateReferenceText)
            ? string.Empty
            : $" partial=\"{FormatRealtimeLogText(decision.NormalizedMessage)}\" recent_full=\"{FormatRealtimeLogText(decision.DuplicateReferenceText)}\"";
        AddRealtimeDebugLog($"[Deduper][Decision] {(decision.ShouldTranslate ? "new" : decision.Reason)}");
        AddRealtimeDebugLog($"dedupe should_translate={decision.ShouldTranslate} reason={decision.Reason}{reference}");
    }

    private static string FormatRealtimeLogText(string value)
    {
        var text = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        }

        return text.Length <= 80 ? text : $"{text[..80]}...";
    }

    private static bool LooksLikeChinese(string value)
    {
        return value.Any(ch => ch is >= '\u4e00' and <= '\u9fff');
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

    private static void LocalizeLanguageComboBox(ComboBox comboBox, string uiLanguage)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag?.ToString() is { } languageCode)
            {
                item.Content = LocalizationService.LocalizedLanguageName(uiLanguage, languageCode);
            }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closeReady)
        {
            e.Cancel = true;
            if (_closeRequested)
            {
                return;
            }

            _closeRequested = true;
            IsEnabled = false;
            _ = CompleteCloseAsync();
            return;
        }

        if (_closeCleanupCompleted)
        {
            base.OnClosing(e);
            return;
        }

        _closeCleanupCompleted = true;
        _ocrTextMaskTrigger.Dispose();
        _ocrService.Dispose();
        _autoOcrCoordinator.Dispose();
        _hotkeyService.Clear(this);
        _overlayWindow.InputSubmitted -= TranslateOverlayInputAsync;
        _settingsWindow?.Close();
        _settingsWindow = null;

        _overlayWindow.Close();

        base.OnClosing(e);
    }

    private async Task CompleteCloseAsync()
    {
        try
        {
            await StopAutoTranslateAsync("window_closing", TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            AppLogService.AppendText(
                "app-errors.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} close_stop_failed {ex}{Environment.NewLine}");
        }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closeReady)
                {
                    return;
                }

                _closeReady = true;
                Close();
            }, DispatcherPriority.Background);
        }
    }

    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;
    private const int WsExTopMost = 0x00000008;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private sealed record HiddenWindowState(Window Window);

    private sealed record SelfOcrFilterResult(
        List<string> AcceptedLines,
        List<string> SkippedLines,
        List<OcrTextLine>? AcceptedTextLines);

    private sealed record RecentOwnOutputText(
        string RawText,
        string NormalizedText,
        string Kind,
        DateTimeOffset CreatedAt);

    private enum OutputItemStatus
    {
        Pending,
        Done,
        Failed
    }

    private sealed class PendingOutputItem
    {
        public PendingOutputItem(
            string messageId,
            string outputKey,
            string scopeKey,
            string sourceText,
            string normalizedSourceText,
            DateTimeOffset createdAt)
        {
            MessageId = messageId;
            OutputKey = outputKey;
            ScopeKey = scopeKey;
            SourceText = sourceText;
            NormalizedSourceText = normalizedSourceText;
            CreatedAt = createdAt;
            UpdatedAt = createdAt;
        }

        public string MessageId { get; }

        public string OutputKey { get; }

        public string ScopeKey { get; }

        public string SourceText { get; }

        public string NormalizedSourceText { get; }

        public DateTimeOffset CreatedAt { get; }

        public DateTimeOffset UpdatedAt { get; set; }

        public OutputItemStatus Status { get; set; } = OutputItemStatus.Pending;

        public Guid OverlayMessageId { get; set; } = Guid.Empty;

        public string DisplayedText { get; set; } = string.Empty;
    }

    private sealed record QueuedTranslationOutput(
        TranslationJob Job,
        string OutputKey,
        PendingOutputItem OutputItem);

    private sealed record RealtimeOcrTriggerFrame(
        Bitmap? Snapshot,
        string Reason,
        long CropMs,
        long MaskMs,
        IReadOnlyList<ulong>? MaskLineHashes = null,
        bool UseFullOcr = true,
        string? FullRescanReason = null,
        int DirtyLineCount = 0,
        int ChangedPixels = 0,
        double DirtyRatio = 0,
        string? CropRegions = null,
        bool TextMaskChanged = false);

    private sealed record LastTranslationEvent(
        string SourceText,
        string TranslatedText,
        string SourceLang,
        double SourceLangConfidence,
        string? Sender,
        string? Timestamp,
        DateTimeOffset DetectedAt);

    private sealed record OverlayInputTarget(
        string Language,
        string SourceKind,
        string FallbackReason,
        string LastSourceLang);

    private sealed class RealtimeOcrDebugState
    {
        public string RunningStatus { get; set; } = "stopped";

        public string LastCaptureTime { get; set; } = "-";

        public string LastTriggerReason { get; set; } = "-";

        public long LastOcrElapsedMs { get; set; }

        public int LastOcrResultLineCount { get; set; }

        public long LastElapsedMs { get; set; }

        public string LastTranslationStatus { get; set; } = "-";

        public string LastSkipReason { get; set; } = "-";

        public string LastError { get; set; } = "-";
    }

    private sealed record OcrTimingSample(
        long CaptureMs,
        long CropMs,
        long MaskMs,
        long PreprocessMs,
        long? OcrDetectMs,
        long? OcrRecognizeMs,
        long OcrTotalMs,
        long ParseMs,
        long TranslateMs,
        long TotalMs);
}
