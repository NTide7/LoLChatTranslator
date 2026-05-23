using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LoLChatTranslator.Services;

namespace LoLChatTranslator;

public partial class RegionSelectorWindow : Window
{
    private Point _startPoint;
    private bool _isSelecting;

    public RegionSelectorWindow(string uiLanguage = "zh-Hans")
    {
        InitializeComponent();
        Title = UiTextLocalizer.Localize(uiLanguage, Title);
        UiTextLocalizer.ApplyTo(this, uiLanguage);

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    public Int32Rect? SelectedRegion { get; private set; }

    public OcrSelectionDebugInfo? SelectionDebugInfo { get; private set; }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(SelectionCanvas);
        _isSelecting = true;

        SelectionRectangle.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRectangle, _startPoint.X);
        Canvas.SetTop(SelectionRectangle, _startPoint.Y);
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;

        CaptureMouse();
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSelecting)
        {
            return;
        }

        var currentPoint = e.GetPosition(SelectionCanvas);
        UpdateSelectionRectangle(currentPoint);
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting)
        {
            return;
        }

        _isSelecting = false;
        ReleaseMouseCapture();

        var endPoint = e.GetPosition(SelectionCanvas);
        var left = Math.Min(_startPoint.X, endPoint.X);
        var top = Math.Min(_startPoint.Y, endPoint.Y);
        var width = Math.Abs(endPoint.X - _startPoint.X);
        var height = Math.Abs(endPoint.Y - _startPoint.Y);

        if (width < 5 || height < 5)
        {
            DialogResult = false;
            Close();
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var topLeftPhysical = PointToScreen(new Point(left, top));
        var bottomRightPhysical = PointToScreen(new Point(left + width, top + height));
        var screenLeft = (int)Math.Round(Math.Min(topLeftPhysical.X, bottomRightPhysical.X));
        var screenTop = (int)Math.Round(Math.Min(topLeftPhysical.Y, bottomRightPhysical.Y));
        var screenRight = (int)Math.Round(Math.Max(topLeftPhysical.X, bottomRightPhysical.X));
        var screenBottom = (int)Math.Round(Math.Max(topLeftPhysical.Y, bottomRightPhysical.Y));
        var screenWidth = Math.Max(1, screenRight - screenLeft);
        var screenHeight = Math.Max(1, screenBottom - screenTop);

        var selectedRegion = new Int32Rect(screenLeft, screenTop, screenWidth, screenHeight);
        SelectedRegion = selectedRegion;
        SelectionDebugInfo = new OcrSelectionDebugInfo(
            new Rect(left, top, width, height),
            new Rect(Left + left, Top + top, width, height),
            selectedRegion,
            dpi.DpiScaleX,
            dpi.DpiScaleY);
        DialogResult = true;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    private void UpdateSelectionRectangle(Point currentPoint)
    {
        var left = Math.Min(_startPoint.X, currentPoint.X);
        var top = Math.Min(_startPoint.Y, currentPoint.Y);
        var width = Math.Abs(currentPoint.X - _startPoint.X);
        var height = Math.Abs(currentPoint.Y - _startPoint.Y);

        Canvas.SetLeft(SelectionRectangle, left);
        Canvas.SetTop(SelectionRectangle, top);
        SelectionRectangle.Width = width;
        SelectionRectangle.Height = height;
    }
}
