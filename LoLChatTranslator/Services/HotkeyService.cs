using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public sealed class HotkeyService
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private readonly List<InputBinding> _registeredBindings = [];
    private readonly Dictionary<int, Action> _registeredGlobalCallbacks = [];
    private HwndSource? _hwndSource;
    private int _nextHotkeyId = 1000;

    public IReadOnlyList<HotkeyRegistrationIssue> RegisterWindowHotkeys(
        Window window,
        AppConfig config,
        Func<Task> manualTranslateAsync,
        Action toggleAutoTranslate,
        Func<Task> translateClipboardAsync,
        Action openSettings,
        Action reselectRegion,
        Action previewRegion,
        Action toggleOverlay,
        Action focusOverlayInput)
    {
        Clear(window);
        var issues = new List<HotkeyRegistrationIssue>();

        AddGesture(window, config.HotkeyConfig.ManualTranslateHotkey, async () => await manualTranslateAsync(), issues);
        AddGesture(window, config.HotkeyConfig.ToggleAutoTranslateHotkey, () => toggleAutoTranslate(), issues);
        AddGesture(window, config.HotkeyConfig.TranslateClipboardHotkey, async () => await translateClipboardAsync(), issues);
        AddGesture(window, config.HotkeyConfig.OpenSettingsHotkey, () => openSettings(), issues);
        AddGesture(window, config.HotkeyConfig.ReselectRegionHotkey, () => reselectRegion(), issues);
        AddGesture(window, config.HotkeyConfig.PreviewRegionHotkey, () => previewRegion(), issues);
        AddGesture(window, config.HotkeyConfig.ToggleOverlayHotkey, () => toggleOverlay(), issues);
        AddGesture(window, config.HotkeyConfig.FocusOverlayInputHotkey, () => focusOverlayInput(), issues);

        return issues;
    }

    public void Clear(Window window)
    {
        foreach (var binding in _registeredBindings)
        {
            window.InputBindings.Remove(binding);
        }

        _registeredBindings.Clear();
        ClearGlobalHotkeys();
    }

    private void AddGesture(Window window, string gestureText, Func<Task> callback, List<HotkeyRegistrationIssue> issues)
    {
        AddGesture(window, gestureText, (Action)(() => { _ = callback(); }), issues);
    }

    private void AddGesture(Window window, string gestureText, Action callback, List<HotkeyRegistrationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(gestureText))
        {
            return;
        }

        try
        {
            var converter = new KeyGestureConverter();
            if (converter.ConvertFromString(gestureText) is not KeyGesture gesture)
            {
                return;
            }

            var command = new RelayCommand(_ => callback());
            var binding = new InputBinding(command, gesture);

            window.InputBindings.Add(binding);
            _registeredBindings.Add(binding);
            RegisterGlobalHotkey(window, gestureText, gesture, callback, issues);
        }
        catch (Exception ex)
        {
            issues.Add(new HotkeyRegistrationIssue(gestureText, $"快捷键格式无效：{ex.Message}", null));
        }
    }

    private void RegisterGlobalHotkey(
        Window window,
        string gestureText,
        KeyGesture gesture,
        Action callback,
        List<HotkeyRegistrationIssue> issues)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            issues.Add(new HotkeyRegistrationIssue(gestureText, "窗口句柄尚未就绪，无法注册全局快捷键。", null));
            return;
        }

        _hwndSource ??= HwndSource.FromHwnd(hwnd);
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource?.AddHook(WndProc);

        var virtualKey = KeyInterop.VirtualKeyFromKey(gesture.Key);
        if (virtualKey == 0)
        {
            issues.Add(new HotkeyRegistrationIssue(gestureText, "无法解析快捷键虚拟键。", null));
            return;
        }

        var modifiers = ToNativeModifiers(gesture.Modifiers) | ModNoRepeat;
        var hotkeyId = _nextHotkeyId++;
        if (!RegisterHotKey(hwnd, hotkeyId, modifiers, (uint)virtualKey))
        {
            var errorCode = Marshal.GetLastWin32Error();
            issues.Add(new HotkeyRegistrationIssue(gestureText, "全局快捷键注册失败，可能已被其他程序占用。", errorCode));
            return;
        }

        _registeredGlobalCallbacks[hotkeyId] = callback;
    }

    public sealed record HotkeyRegistrationIssue(string GestureText, string Reason, int? ErrorCode);

    private void ClearGlobalHotkeys()
    {
        var hwnd = _hwndSource?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            foreach (var hotkeyId in _registeredGlobalCallbacks.Keys.ToList())
            {
                UnregisterHotKey(hwnd, hotkeyId);
            }
        }

        _registeredGlobalCallbacks.Clear();
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey)
        {
            return IntPtr.Zero;
        }

        var hotkeyId = wParam.ToInt32();
        if (!_registeredGlobalCallbacks.TryGetValue(hotkeyId, out var callback))
        {
            return IntPtr.Zero;
        }

        handled = true;
        callback();
        return IntPtr.Zero;
    }

    private static uint ToNativeModifiers(ModifierKeys modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            result |= ModAlt;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            result |= ModControl;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            result |= ModShift;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            result |= ModWin;
        }

        return result;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;

        public RelayCommand(Action<object?> execute)
        {
            _execute = execute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            _execute(parameter);
        }
    }
}
