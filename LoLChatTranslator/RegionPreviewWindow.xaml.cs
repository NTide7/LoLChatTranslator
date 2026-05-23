using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LoLChatTranslator.Services;

namespace LoLChatTranslator;

public partial class RegionPreviewWindow : Window
{
    private readonly Int32Rect _region;

    public RegionPreviewWindow(Int32Rect region, string uiLanguage = "zh-Hans")
    {
        InitializeComponent();
        Title = UiTextLocalizer.Localize(uiLanguage, Title);
        UiTextLocalizer.ApplyTo(this, uiLanguage);

        _region = region;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var topLeft = PointFromScreen(new Point(_region.X, _region.Y));
        var bottomRight = PointFromScreen(new Point(_region.X + _region.Width, _region.Y + _region.Height));

        Canvas.SetLeft(RegionRectangle, Math.Min(topLeft.X, bottomRight.X));
        Canvas.SetTop(RegionRectangle, Math.Min(topLeft.Y, bottomRight.Y));
        RegionRectangle.Width = Math.Max(1, Math.Abs(bottomRight.X - topLeft.X));
        RegionRectangle.Height = Math.Max(1, Math.Abs(bottomRight.Y - topLeft.Y));

        PositionInstructionAboveRegion();
        Focus();
    }

    private void PositionInstructionAboveRegion()
    {
        InstructionBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var regionLeft = Canvas.GetLeft(RegionRectangle);
        var regionTop = Canvas.GetTop(RegionRectangle);
        var instructionWidth = InstructionBorder.DesiredSize.Width;
        var instructionHeight = InstructionBorder.DesiredSize.Height;

        var left = regionLeft + (RegionRectangle.Width / 2) - (instructionWidth / 2);
        var top = regionTop - instructionHeight - 10;

        left = Math.Clamp(left, 8, Math.Max(8, ActualWidth - instructionWidth - 8));
        top = Math.Clamp(top, 8, Math.Max(8, ActualHeight - instructionHeight - 8));

        Canvas.SetLeft(InstructionBorder, left);
        Canvas.SetTop(InstructionBorder, top);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Close();
    }
}
