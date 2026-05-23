using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LoLChatTranslator.Models;
using LoLChatTranslator.Services;

namespace LoLChatTranslator;

public partial class OverlayWindow : Window
{
    private const int ResizeBorderThickness = 8;
    private const int WmNcHitTest = 0x0084;
    private const int HtClient = 1;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    private readonly ObservableCollection<OverlayMessage> _messages = [];
    private HwndSource? _hwndSource;
    private AppConfig _config = AppConfig.CreateDefault();
    private bool _isLoaded;
    private bool _isSubmittingInput;

    public bool IsExcludedFromCapture { get; private set; }

    public event Func<string, Task>? InputSubmitted;

    public OverlayWindow()
    {
        InitializeComponent();
        MessageItemsControl.ItemsSource = _messages;
    }

    public void ApplyConfig(AppConfig config)
    {
        _config = config;
        Topmost = config.OverlayConfig.AlwaysOnTop;
        ApplyVisualSettings();
        TrimMessages();

        if (_isLoaded)
        {
            ApplyClickThrough(ShouldEnableClickThrough());
            ApplyCaptureExclusion();
        }
    }

    public Guid AddMessage(string originalText, string translatedText)
    {
        return AddMessage(originalText, translatedText, ChatChannel.Unknown);
    }

    public Guid AddMessage(string originalText, string translatedText, ChatChannel channel)
    {
        if (!Dispatcher.CheckAccess())
        {
            return InvokeOnUi(() => AddMessage(originalText, translatedText, channel), Guid.Empty);
        }

        if (!TryPrepareOverlayTranslation(translatedText, out var displayText))
        {
            return Guid.Empty;
        }

        var message = new OverlayMessage(
            originalText,
            displayText,
            GetHeaderBrush(channel),
            GetTextBrush(channel));
        _messages.Add(message);
        TrimMessages();
        ScrollToLatestMessage();
        return message.Id;
    }

    public IReadOnlyList<Guid> AddMessagesInOrder(IReadOnlyList<OverlayMessageRequest> requests)
    {
        if (!Dispatcher.CheckAccess())
        {
            return InvokeOnUi(() => AddMessagesInOrder(requests), Array.Empty<Guid>());
        }

        var ids = new List<Guid>(requests.Count);
        foreach (var request in requests)
        {
            if (!TryPrepareOverlayTranslation(request.TranslatedText, out var displayText))
            {
                ids.Add(Guid.Empty);
                continue;
            }

            var message = new OverlayMessage(
                request.OriginalText,
                displayText,
                GetHeaderBrush(request.Channel),
                GetTextBrush(request.Channel));
            _messages.Add(message);
            ids.Add(message.Id);
        }

        TrimMessages();
        ScrollToLatestMessage();
        return ids;
    }

    public void UpdateTranslation(Guid messageId, string translatedText)
    {
        if (!Dispatcher.CheckAccess())
        {
            BeginOnUi(() => UpdateTranslation(messageId, translatedText));
            return;
        }

        var message = _messages.FirstOrDefault(item => item.Id == messageId);
        if (message is not null)
        {
            if (TryPrepareOverlayTranslation(translatedText, out var displayText))
            {
                message.TranslatedText = displayText;
            }
            else
            {
                _messages.Remove(message);
            }
        }
    }

    public void RemoveMessage(Guid messageId)
    {
        if (!Dispatcher.CheckAccess())
        {
            BeginOnUi(() => RemoveMessage(messageId));
            return;
        }

        var message = _messages.FirstOrDefault(item => item.Id == messageId);
        if (message is not null)
        {
            _messages.Remove(message);
        }
    }

    public void ClearMessages()
    {
        if (!Dispatcher.CheckAccess())
        {
            BeginOnUi(ClearMessages);
            return;
        }

        _messages.Clear();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        AttachResizeHook();
        ApplyConfig(_config);
        ApplyCaptureExclusion();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsResizeBorder(e.GetPosition(this)) || IsInputElement(e.OriginalSource) || ShouldEnableClickThrough())
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // Ignore drag failures caused by fast clicks.
        }
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        Cursor = ShouldEnableClickThrough()
            ? null
            : CursorForHitTest(GetResizeHitTest(e.GetPosition(this)));
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        Cursor = null;
    }

    private void ApplyVisualSettings()
    {
        var opacity = Math.Clamp(_config.OverlayConfig.Opacity, 0.2, 1);
        var alpha = (byte)Math.Round(opacity * 255);
        RootBorder.Background = CreateAlphaBrush(_config.OverlayConfig.BackgroundColor, alpha, Color.FromRgb(17, 24, 39));
        MessageItemsControl.FontSize = Math.Clamp(_config.OverlayConfig.FontSize, 10, 36);
        InputPanel.Visibility = _config.TranslateConfig.EnableOverlayInput ? Visibility.Visible : Visibility.Collapsed;
        InputPanel.Background = CreateAlphaBrush(_config.OverlayConfig.InputBackgroundColor, 130, Color.FromRgb(15, 23, 42));
        InputPanel.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
        OverlayInputTextBox.Background = CreateAlphaBrush(_config.OverlayConfig.InputBackgroundColor, 105, Color.FromRgb(15, 23, 42));
        OverlayInputTextBox.Foreground = Brushes.WhiteSmoke;
        OverlayInputTextBox.CaretBrush = Brushes.White;
        OverlayInputSubmitButton.Background = new SolidColorBrush(Color.FromArgb(135, 255, 255, 255));
        OverlayInputSubmitButton.Foreground = Brushes.Black;
        OverlayInputSubmitButton.BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
        OverlayInputSubmitButton.Content = LocalizationService.T(_config.UiLanguage, "OverlayTranslate");
    }

    public void FocusOverlayInput(bool selectAll = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            BeginOnUi(() => FocusOverlayInput(selectAll));
            return;
        }

        InputPanel.Visibility = Visibility.Visible;
        Activate();
        FocusInputBox(selectAll);
    }

    public void SetInputTargetStatus(string targetDisplayName, string sourceDisplayName)
    {
        if (!Dispatcher.CheckAccess())
        {
            BeginOnUi(() => SetInputTargetStatus(targetDisplayName, sourceDisplayName));
            return;
        }

        OverlayInputTargetStatusTextBlock.Text = string.Format(
            LocalizationService.T(_config.UiLanguage, "OverlayInputTargetStatus"),
            targetDisplayName,
            sourceDisplayName);
    }

    private T InvokeOnUi<T>(Func<T> action, T fallback)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return fallback;
        }

        try
        {
            return Dispatcher.Invoke(action);
        }
        catch
        {
            return fallback;
        }
    }

    private void BeginOnUi(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(action);
        }
        catch
        {
            // Window shutdown can race with background translation completion.
        }
    }

    private void TrimMessages()
    {
        var maxLines = Math.Max(1, _config.OverlayConfig.MaxLines);
        while (_messages.Count > maxLines)
        {
            _messages.RemoveAt(0);
        }
    }

    private void ScrollToLatestMessage()
    {
        Dispatcher.BeginInvoke(
            () => MessageScrollViewer.ScrollToEnd(),
            DispatcherPriority.Background);
    }

    private Brush GetHeaderBrush(ChatChannel channel)
    {
        return channel switch
        {
            ChatChannel.Team => CreateBrush(_config.OverlayConfig.TeamHeaderColor, Brushes.LightSkyBlue),
            ChatChannel.All => CreateBrush(_config.OverlayConfig.AllHeaderColor, Brushes.LightCoral),
            ChatChannel.Party => CreateBrush(_config.OverlayConfig.PartyHeaderColor, Brushes.Plum),
            ChatChannel.System => CreateBrush(_config.OverlayConfig.SystemHeaderColor, Brushes.Gold),
            _ => CreateBrush(_config.OverlayConfig.UnknownHeaderColor, Brushes.LightGray)
        };
    }

    private Brush GetTextBrush(ChatChannel channel)
    {
        return channel switch
        {
            ChatChannel.Team => CreateBrush(_config.OverlayConfig.TeamTextColor, Brushes.White),
            ChatChannel.All => CreateBrush(_config.OverlayConfig.AllTextColor, Brushes.White),
            ChatChannel.Party => CreateBrush(_config.OverlayConfig.PartyTextColor, Brushes.White),
            ChatChannel.System => CreateBrush(_config.OverlayConfig.SystemTextColor, Brushes.LightGoldenrodYellow),
            _ => CreateBrush(_config.OverlayConfig.UnknownTextColor, Brushes.White)
        };
    }

    private bool TryPrepareOverlayTranslation(string translatedText, out string displayText)
    {
        displayText = translatedText.Trim();
        var targetLanguage = _config.TranslateConfig.TargetLanguage;
        if (TranslatorErrorSanitizer.IsErrorResult(displayText))
        {
            displayText = string.Empty;
            return false;
        }

        if (OcrTextFixer.TryTranslateBuiltInPhrase(displayText, targetLanguage, out var localTranslation))
        {
            displayText = localTranslation;
            return true;
        }

        if (!TranslatorLanguage.IsAnyChinese(targetLanguage))
        {
            return !string.IsNullOrWhiteSpace(displayText);
        }

        if (LooksLikeChinese(displayText)
            && !OcrTextFixer.HasSuspiciousEnglishResidueForChineseTarget(displayText))
        {
            return true;
        }

        displayText = string.Empty;
        return false;
    }

    private static bool LooksLikeChinese(string value)
    {
        return value.Any(ch => ch is >= '\u4e00' and <= '\u9fff');
    }

    private static Brush CreateBrush(string color, Brush fallback)
    {
        try
        {
            var brush = (Brush?)new BrushConverter().ConvertFromString(color);
            if (brush is null)
            {
                return fallback;
            }

            brush.Freeze();
            return brush;
        }
        catch
        {
            return fallback;
        }
    }

    private static Brush CreateAlphaBrush(string color, byte alpha, Color fallback)
    {
        try
        {
            var parsed = ColorConverter.ConvertFromString(color);
            if (parsed is not Color value)
            {
                return new SolidColorBrush(Color.FromArgb(alpha, fallback.R, fallback.G, fallback.B));
            }

            value.A = alpha;
            var brush = new SolidColorBrush(value);
            brush.Freeze();
            return brush;
        }
        catch
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, fallback.R, fallback.G, fallback.B));
            brush.Freeze();
            return brush;
        }
    }

    private bool ShouldEnableClickThrough()
    {
        return _config.OverlayConfig.ClickThrough && !_config.TranslateConfig.EnableOverlayInput;
    }

    private static bool IsInputElement(object originalSource)
    {
        if (originalSource is not DependencyObject current)
        {
            return false;
        }

        while (current is not null)
        {
            if (current is System.Windows.Controls.TextBox or System.Windows.Controls.Button)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private async void OverlayInputSubmitButton_Click(object sender, RoutedEventArgs e)
    {
        await SubmitOverlayInputAsync();
    }

    private async void OverlayInputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await SubmitOverlayInputAsync();
    }

    private async Task SubmitOverlayInputAsync()
    {
        if (_isSubmittingInput)
        {
            return;
        }

        var text = OverlayInputTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text) || InputSubmitted is null)
        {
            FocusInputBox();
            return;
        }

        _isSubmittingInput = true;
        SetInputBusy(true);
        try
        {
            await InputSubmitted.Invoke(text);
            OverlayInputTextBox.Clear();
        }
        finally
        {
            _isSubmittingInput = false;
            SetInputBusy(false);
            FocusInputBox();
        }
    }

    private void SetInputBusy(bool isBusy)
    {
        OverlayInputTextBox.IsReadOnly = isBusy;
        OverlayInputSubmitButton.IsEnabled = !isBusy;
    }

    private void FocusInputBox(bool selectAll = false)
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                OverlayInputTextBox.Focus();
                if (selectAll)
                {
                    OverlayInputTextBox.SelectAll();
                }
                else
                {
                    OverlayInputTextBox.CaretIndex = OverlayInputTextBox.Text.Length;
                }
            },
            DispatcherPriority.Input);
    }

    private void ApplyClickThrough(bool enabled)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var style = GetWindowLong(hwnd, GwlExStyle);
            style |= WsExToolWindow;

            if (enabled)
            {
                style |= WsExTransparent;
            }
            else
            {
                style &= ~WsExTransparent;
            }

            SetWindowLong(hwnd, GwlExStyle, style);
        }
        catch
        {
            // Some Windows builds reject style changes during window teardown.
        }
    }

    private void ApplyCaptureExclusion()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            if (!_config.OverlayConfig.ExcludeFromScreenCapture)
            {
                SetWindowDisplayAffinity(hwnd, WdaNone);
                IsExcludedFromCapture = false;
                return;
            }

            // Exclude only this helper window from normal screen capture when the OS supports it.
            // This avoids OCR reading our own overlay without hiding/showing every capture tick.
            IsExcludedFromCapture = SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
        }
        catch
        {
            IsExcludedFromCapture = false;
        }
    }

    private void AttachResizeHook()
    {
        if (_hwndSource is not null)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmNcHitTest || ShouldEnableClickThrough())
        {
            return IntPtr.Zero;
        }

        var screenPoint = new Point(GetSignedLoWord(lParam), GetSignedHiWord(lParam));
        var hitTest = GetResizeHitTest(PointFromScreen(screenPoint));
        if (hitTest == HtClient)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(hitTest);
    }

    private bool IsResizeBorder(Point point)
    {
        return GetResizeHitTest(point) != HtClient;
    }

    private int GetResizeHitTest(Point point)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return HtClient;
        }

        var left = point.X >= 0 && point.X <= ResizeBorderThickness;
        var right = point.X <= ActualWidth && point.X >= ActualWidth - ResizeBorderThickness;
        var top = point.Y >= 0 && point.Y <= ResizeBorderThickness;
        var bottom = point.Y <= ActualHeight && point.Y >= ActualHeight - ResizeBorderThickness;

        if (top && left)
        {
            return HtTopLeft;
        }

        if (top && right)
        {
            return HtTopRight;
        }

        if (bottom && left)
        {
            return HtBottomLeft;
        }

        if (bottom && right)
        {
            return HtBottomRight;
        }

        if (left)
        {
            return HtLeft;
        }

        if (right)
        {
            return HtRight;
        }

        if (top)
        {
            return HtTop;
        }

        return bottom ? HtBottom : HtClient;
    }

    private static Cursor? CursorForHitTest(int hitTest)
    {
        return hitTest switch
        {
            HtLeft or HtRight => Cursors.SizeWE,
            HtTop or HtBottom => Cursors.SizeNS,
            HtTopLeft or HtBottomRight => Cursors.SizeNWSE,
            HtTopRight or HtBottomLeft => Cursors.SizeNESW,
            _ => null
        };
    }

    private static int GetSignedLoWord(IntPtr value)
    {
        return unchecked((short)((long)value & 0xffff));
    }

    private static int GetSignedHiWord(IntPtr value)
    {
        return unchecked((short)(((long)value >> 16) & 0xffff));
    }

    protected override void OnClosed(EventArgs e)
    {
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        base.OnClosed(e);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    private sealed class OverlayMessage : INotifyPropertyChanged
    {
        private string _translatedText;

        public OverlayMessage(string originalText, string translatedText, Brush headerBrush, Brush textBrush)
        {
            OriginalText = originalText;
            _translatedText = translatedText;
            HeaderBrush = headerBrush;
            TextBrush = textBrush;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public Guid Id { get; } = Guid.NewGuid();

        public string OriginalText { get; }

        public string TranslatedText
        {
            get => _translatedText;
            set
            {
                if (_translatedText == value)
                {
                    return;
                }

                _translatedText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TranslatedText)));
            }
        }

        public Brush HeaderBrush { get; }

        public Brush TextBrush { get; }
    }
}

public sealed record OverlayMessageRequest(
    string OriginalText,
    string TranslatedText,
    ChatChannel Channel);
