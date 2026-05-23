using System.Windows;
using LoLChatTranslator.Services;

namespace LoLChatTranslator;

public partial class UpdateAvailableWindow : Window
{
    public UpdateAvailableWindow(string version, string uiLanguage = "zh-Hans")
    {
        InitializeComponent();
        Title = UiTextLocalizer.Localize(uiLanguage, Title);
        UiTextLocalizer.ApplyTo(this, uiLanguage);
        MessageTextBlock.Text = UiTextLocalizer.Text(
            uiLanguage,
            $"发现新版本（{version}），自动跳转至Github下载",
            $"發現新版本（{version}），將自動跳轉至 Github 下載",
            $"A new version is available ({version}). Open GitHub to download it?",
            $"새 버전({version})이 있습니다. GitHub 다운로드 페이지를 열까요?",
            $"新しいバージョン（{version}）があります。GitHub のダウンロードページを開きますか？",
            $"Có phiên bản mới ({version}). Mở GitHub để tải xuống?");
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
