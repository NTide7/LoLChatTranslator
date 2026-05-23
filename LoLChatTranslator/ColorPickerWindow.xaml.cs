using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LoLChatTranslator.Services;

namespace LoLChatTranslator;

public partial class ColorPickerWindow : Window
{
    private bool _isUpdating;

    public ColorPickerWindow(string initialColor, string uiLanguage = "zh-Hans")
    {
        InitializeComponent();
        Title = UiTextLocalizer.Localize(uiLanguage, Title);
        UiTextLocalizer.ApplyTo(this, uiLanguage);
        SelectedColorHex = NormalizeColor(initialColor);
        SetColor(ParseColor(SelectedColorHex));
    }

    public string SelectedColorHex { get; private set; }

    private void SwatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            SetColor(ParseColor(color));
        }
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdating || !IsLoaded)
        {
            return;
        }

        SetColor(Color.FromRgb(
            (byte)Math.Round(RedSlider.Value),
            (byte)Math.Round(GreenSlider.Value),
            (byte)Math.Round(BlueSlider.Value)));
    }

    private void HexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdating || !IsLoaded)
        {
            return;
        }

        var normalized = NormalizeColor(HexTextBox.Text, fallback: string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        SetColor(ParseColor(normalized));
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedColorHex = NormalizeColor(HexTextBox.Text);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void SetColor(Color color)
    {
        _isUpdating = true;
        try
        {
            RedSlider.Value = color.R;
            GreenSlider.Value = color.G;
            BlueSlider.Value = color.B;
            RedValueTextBlock.Text = color.R.ToString();
            GreenValueTextBlock.Text = color.G.ToString();
            BlueValueTextBlock.Text = color.B.ToString();
            SelectedColorHex = ToHex(color);
            HexTextBox.Text = SelectedColorHex;
            ColorPreviewBorder.Background = new SolidColorBrush(color);
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private static Color ParseColor(string value)
    {
        var normalized = NormalizeColor(value);
        var hex = normalized[1..];
        var red = Convert.ToByte(hex[..2], 16);
        var green = Convert.ToByte(hex.Substring(2, 2), 16);
        var blue = Convert.ToByte(hex.Substring(4, 2), 16);
        return Color.FromRgb(red, green, blue);
    }

    private static string NormalizeColor(string value, string fallback = "#FFFFFF")
    {
        var text = value.Trim();
        if (text.Length == 9 && text[0] == '#')
        {
            text = $"#{text[3..]}";
        }

        if (text.Length != 7 || text[0] != '#' || !text.Skip(1).All(Uri.IsHexDigit))
        {
            return fallback;
        }

        return text.ToUpperInvariant();
    }

    private static string ToHex(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}
