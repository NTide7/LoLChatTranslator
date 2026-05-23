using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;

namespace LoLChatTranslator.Services;

public static class OcrEnvironmentInstallRecovery
{
    public const string InstallOcrDependenciesArgument = "--install-ocr-deps";

    public static string? PromptForAlternativeDirectory(
        Window owner,
        string uiLanguage,
        string currentDirectory,
        string failureMessage)
    {
        var choosePath = MessageBox.Show(
            owner,
            UiTextLocalizer.Text(
                uiLanguage,
                $"OCR 环境安装失败。当前目录可能不可写：{currentDirectory}{Environment.NewLine}{Environment.NewLine}建议选择一个当前用户可写的位置，例如非 Program Files 的文件夹。是否现在选择新的 OCR 环境安装位置？{Environment.NewLine}{Environment.NewLine}{Shorten(failureMessage)}",
                $"OCR 環境安裝失敗。目前目錄可能無法寫入：{currentDirectory}{Environment.NewLine}{Environment.NewLine}建議選擇目前使用者可寫入的位置，例如非 Program Files 的資料夾。是否現在選擇新的 OCR 環境安裝位置？{Environment.NewLine}{Environment.NewLine}{Shorten(failureMessage)}",
                $"OCR environment installation failed. The current directory may not be writable: {currentDirectory}{Environment.NewLine}{Environment.NewLine}Choose a location writable by the current user, such as a folder outside Program Files. Choose a new OCR environment location now?{Environment.NewLine}{Environment.NewLine}{Shorten(failureMessage)}",
                $"OCR 환경 설치에 실패했습니다. 현재 디렉터리에 쓸 수 없을 수 있습니다: {currentDirectory}{Environment.NewLine}{Environment.NewLine}Program Files가 아닌 현재 사용자가 쓸 수 있는 위치를 선택하는 것이 좋습니다. 새 OCR 환경 위치를 지금 선택할까요?{Environment.NewLine}{Environment.NewLine}{Shorten(failureMessage)}",
                $"OCR 環境のインストールに失敗しました。現在のディレクトリに書き込めない可能性があります: {currentDirectory}{Environment.NewLine}{Environment.NewLine}Program Files 以外など、現在のユーザーが書き込める場所を選んでください。新しい OCR 環境の場所を選択しますか？{Environment.NewLine}{Environment.NewLine}{Shorten(failureMessage)}",
                $"Cài môi trường OCR thất bại. Thư mục hiện tại có thể không ghi được: {currentDirectory}{Environment.NewLine}{Environment.NewLine}Hãy chọn một vị trí người dùng hiện tại có quyền ghi, ví dụ ngoài Program Files. Chọn vị trí OCR mới ngay bây giờ?{Environment.NewLine}{Environment.NewLine}{Shorten(failureMessage)}"),
            UiTextLocalizer.Text(
                uiLanguage,
                "选择 OCR 环境安装位置",
                "選擇 OCR 環境安裝位置",
                "Choose OCR Environment Location",
                "OCR 환경 위치 선택",
                "OCR 環境の場所を選択",
                "Chọn vị trí môi trường OCR"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);

        if (choosePath != MessageBoxResult.Yes)
        {
            return null;
        }

        var initialDirectory = Directory.Exists(currentDirectory)
            ? currentDirectory
            : PythonEnvironmentService.DefaultOcrEnvironmentDirectory;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = UiTextLocalizer.Text(
                uiLanguage,
                "选择 OCR 环境安装位置",
                "選擇 OCR 環境安裝位置",
                "Choose OCR Environment Location",
                "OCR 환경 위치 선택",
                "OCR 環境の場所を選択",
                "Chọn vị trí môi trường OCR"),
            InitialDirectory = initialDirectory
        };

        if (dialog.ShowDialog(owner) != true)
        {
            return null;
        }

        return PythonEnvironmentService.NormalizeOcrEnvironmentDirectory(dialog.FolderName);
    }

    public static bool ShouldOfferAdministratorRestart(OcrDependencyInstallResult result)
    {
        return !result.Succeeded && (result.RequiresElevation || LooksLikePermissionFailure(result.Message));
    }

    public static bool PromptRestartAsAdministrator(Window owner, string uiLanguage, string failureMessage)
    {
        if (IsRunningAsAdministrator())
        {
            MessageBox.Show(
                owner,
                UiTextLocalizer.Text(
                    uiLanguage,
                    $"当前程序已经以管理员身份运行，但 OCR 环境仍安装失败。请换一个可写目录后重试。{Environment.NewLine}{Environment.NewLine}{Shorten(failureMessage)}",
                    $"目前程式已以系統管理員身分執行，但 OCR 環境仍安裝失敗。請換一個可寫入目錄後重試。{Environment.NewLine}{Environment.NewLine}{Shorten(failureMessage)}",
                    $"The app is already running as administrator, but OCR environment installation still failed. Choose a writable directory and retry.{Environment.NewLine}{Environment.NewLine}{Shorten(failureMessage)}",
                    $"앱이 이미 관리자 권한으로 실행 중이지만 OCR 환경 설치가 실패했습니다. 쓸 수 있는 디렉터리를 선택한 뒤 다시 시도하세요.{Environment.NewLine}{Environment.NewLine}{Shorten(failureMessage)}",
                    $"アプリは既に管理者として実行されていますが、OCR 環境のインストールに失敗しました。書き込み可能な場所を選んで再試行してください。{Environment.NewLine}{Environment.NewLine}{Shorten(failureMessage)}",
                    $"Ứng dụng đã chạy bằng quyền quản trị nhưng cài môi trường OCR vẫn thất bại. Hãy chọn thư mục ghi được rồi thử lại.{Environment.NewLine}{Environment.NewLine}{Shorten(failureMessage)}"),
                UiTextLocalizer.Text(uiLanguage, "OCR 环境安装失败", "OCR 環境安裝失敗", "OCR Environment Install Failed", "OCR 환경 설치 실패", "OCR 環境のインストール失敗", "Cài môi trường OCR thất bại"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var restart = MessageBox.Show(
            owner,
            UiTextLocalizer.Text(
                uiLanguage,
                $"该位置仍然无法写入。是否申请管理员权限并重启程序，然后自动继续检测/安装 OCR 环境？{Environment.NewLine}{Environment.NewLine}{Shorten(failureMessage)}",
                $"該位置仍然無法寫入。是否申請系統管理員權限並重新啟動程式，然後自動繼續偵測/安裝 OCR 環境？{Environment.NewLine}{Environment.NewLine}{Shorten(failureMessage)}",
                $"The selected location is still not writable. Request administrator permission, restart the app, and automatically continue OCR environment setup?{Environment.NewLine}{Environment.NewLine}{Shorten(failureMessage)}",
                $"선택한 위치에 여전히 쓸 수 없습니다. 관리자 권한을 요청하고 앱을 다시 시작한 뒤 OCR 환경 설정을 자동으로 계속할까요?{Environment.NewLine}{Environment.NewLine}{Shorten(failureMessage)}",
                $"選択した場所にまだ書き込めません。管理者権限を要求してアプリを再起動し、OCR 環境の設定を自動的に続行しますか？{Environment.NewLine}{Environment.NewLine}{Shorten(failureMessage)}",
                $"Vị trí đã chọn vẫn không ghi được. Yêu cầu quyền quản trị, khởi động lại ứng dụng và tự động tiếp tục cài môi trường OCR?{Environment.NewLine}{Environment.NewLine}{Shorten(failureMessage)}"),
            UiTextLocalizer.Text(uiLanguage, "申请管理员权限", "申請系統管理員權限", "Request Administrator Permission", "관리자 권한 요청", "管理者権限を要求", "Yêu cầu quyền quản trị"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        return restart == MessageBoxResult.Yes && TryRestartAsAdministrator(owner, uiLanguage);
    }

    public static bool LooksLikePermissionFailure(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("WinError 5", StringComparison.OrdinalIgnoreCase)
            || text.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase)
            || text.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryRestartAsAdministrator(Window owner, string uiLanguage)
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            {
                executable = Process.GetCurrentProcess().MainModule?.FileName;
            }

            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            {
                throw new InvalidOperationException("无法定位当前程序路径。");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = InstallOcrDependenciesArgument,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
            Application.Current.Shutdown();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                owner,
                UiTextLocalizer.Text(
                    uiLanguage,
                    $"无法申请管理员权限：{ex.Message}",
                    $"無法申請系統管理員權限：{ex.Message}",
                    $"Could not request administrator permission: {ex.Message}",
                    $"관리자 권한을 요청할 수 없습니다: {ex.Message}",
                    $"管理者権限を要求できません: {ex.Message}",
                    $"Không thể yêu cầu quyền quản trị: {ex.Message}"),
                UiTextLocalizer.Text(uiLanguage, "申请管理员权限失败", "申請系統管理員權限失敗", "Administrator Request Failed", "관리자 권한 요청 실패", "管理者権限の要求失敗", "Yêu cầu quyền quản trị thất bại"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string Shorten(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 700 ? normalized : normalized[..700] + "...";
    }
}
