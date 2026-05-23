using System.Windows;
using System.Windows.Input;
using LoLChatTranslator.Services;

namespace LoLChatTranslator;

public partial class HotkeyCaptureWindow : Window
{
    private readonly string _uiLanguage;

    public HotkeyCaptureWindow(string uiLanguage = "zh-Hans")
    {
        _uiLanguage = LocalizationService.NormalizeLanguage(uiLanguage);
        InitializeComponent();
        Title = UiTextLocalizer.Localize(_uiLanguage, Title);
        UiTextLocalizer.ApplyTo(this, _uiLanguage);
    }

    public string? CapturedHotkey { get; private set; }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Focus();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        var key = e.Key == Key.System
            ? e.SystemKey
            : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;

        if (key == Key.Escape)
        {
            DialogResult = false;
            Close();
            return;
        }

        if (IsModifierKey(key))
        {
            HintTextBlock.Text = UiTextLocalizer.Text(
                _uiLanguage,
                "请再按一个非 Ctrl / Alt / Shift 的按键。",
                "請再按一個非 Ctrl / Alt / Shift 的按鍵。",
                "Press one more key that is not Ctrl / Alt / Shift.",
                "Ctrl / Alt / Shift가 아닌 키를 하나 더 누르세요.",
                "Ctrl / Alt / Shift 以外のキーをもう 1 つ押してください。",
                "Nhấn thêm một phím không phải Ctrl / Alt / Shift.");
            return;
        }

        var modifiers = Keyboard.Modifiers;

        try
        {
            var gesture = new KeyGesture(key, modifiers);
            var converter = new KeyGestureConverter();
            var hotkeyText = converter.ConvertToString(gesture);

            if (string.IsNullOrWhiteSpace(hotkeyText))
            {
                HintTextBlock.Text = UiTextLocalizer.Text(
                    _uiLanguage,
                    "这个按键组合暂不支持，请换一个。",
                    "這個按鍵組合暫不支援，請換一個。",
                    "This key combination is not supported. Try another one.",
                    "이 키 조합은 지원되지 않습니다. 다른 조합을 사용하세요.",
                    "このキー組み合わせは未対応です。別のものを試してください。",
                    "Tổ hợp phím này chưa được hỗ trợ. Hãy chọn tổ hợp khác.");
                return;
            }

            CapturedHotkey = hotkeyText;
            DialogResult = true;
            Close();
        }
        catch
        {
            HintTextBlock.Text = UiTextLocalizer.Text(
                _uiLanguage,
                "这个按键组合暂不支持。建议使用 F8、F9 或 Ctrl+Shift+字母。",
                "這個按鍵組合暫不支援。建議使用 F8、F9 或 Ctrl+Shift+字母。",
                "This key combination is not supported. Try F8, F9, or Ctrl+Shift+letter.",
                "이 키 조합은 지원되지 않습니다. F8, F9 또는 Ctrl+Shift+문자를 권장합니다.",
                "このキー組み合わせは未対応です。F8、F9、または Ctrl+Shift+文字をおすすめします。",
                "Tổ hợp phím này chưa được hỗ trợ. Nên dùng F8, F9 hoặc Ctrl+Shift+chữ.");
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin;
    }
}
