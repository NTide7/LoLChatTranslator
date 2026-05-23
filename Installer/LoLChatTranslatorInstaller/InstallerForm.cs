using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LoLChatTranslatorInstaller;

public sealed class InstallerForm : Form
{
    private const string AppFolderName = "LoLChatTranslator";
    private const string AppExeName = "LoLChatTranslator.exe";
    private const string AppDisplayName = "LOL Chat OCR Translator";
    private const string AppVersion = "1.0.1-dev";
    private const string AppPublisher = "NTide7";
    private const string AppProjectUrl = "https://github.com/NTide7/LoLChatTranslator";
    private const string PayloadResourceName = "Payload.zip";
    private const string UninstallScriptName = "Uninstall-LoLChatTranslator.ps1";
    private const string UninstallRegistrySubKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LoLChatTranslator";

    private readonly InstallerText _text = InstallerText.ForCurrentCulture();
    private readonly TextBox _installParentTextBox = new();
    private readonly CheckBox _desktopShortcutCheckBox = new();
    private readonly Button _browseButton = new();
    private readonly Button _installButton = new();
    private readonly Button _cancelButton = new();
    private readonly Label _statusLabel = new();
    private readonly ProgressBar _progressBar = new();

    public InstallerForm()
    {
        BuildUi();
    }

    private void BuildUi()
    {
        Text = _text.WindowTitle;
        Icon = LoadInstallerIcon();
        Width = 640;
        Height = 330;
        MinimumSize = new Size(580, 300);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 1,
            RowCount = 9
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var titleLabel = new Label
        {
            AutoSize = true,
            Text = _text.Title,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 12)
        };

        var locationLabel = new Label
        {
            AutoSize = true,
            Text = _text.InstallLocationLabel,
            Margin = new Padding(0, 0, 0, 8)
        };

        var locationPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        };
        locationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        locationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _installParentTextBox.Dock = DockStyle.Fill;
        _installParentTextBox.Text = GetDefaultInstallParent();

        _browseButton.AutoSize = true;
        _browseButton.Text = _text.Browse;
        _browseButton.Margin = new Padding(8, 0, 0, 0);
        _browseButton.Click += BrowseButton_Click;

        locationPanel.Controls.Add(_installParentTextBox, 0, 0);
        locationPanel.Controls.Add(_browseButton, 1, 0);

        var finalPathLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Text = _text.FinalPathHint,
            Margin = new Padding(0, 0, 0, 12)
        };

        _desktopShortcutCheckBox.AutoSize = true;
        _desktopShortcutCheckBox.Checked = true;
        _desktopShortcutCheckBox.Text = _text.CreateDesktopShortcut;
        _desktopShortcutCheckBox.Margin = new Padding(0, 0, 0, 14);

        _progressBar.Dock = DockStyle.Top;
        _progressBar.Height = 18;
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Visible = false;

        _statusLabel.AutoSize = true;
        _statusLabel.ForeColor = Color.DimGray;
        _statusLabel.Margin = new Padding(0, 8, 0, 0);

        var actionPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true
        };

        _installButton.AutoSize = true;
        _installButton.Text = _text.Install;
        _installButton.BackColor = Color.FromArgb(5, 150, 105);
        _installButton.ForeColor = Color.White;
        _installButton.FlatStyle = FlatStyle.Flat;
        _installButton.Padding = new Padding(18, 4, 18, 4);
        _installButton.Click += InstallButton_Click;

        _cancelButton.AutoSize = true;
        _cancelButton.Text = _text.Cancel;
        _cancelButton.Padding = new Padding(18, 4, 18, 4);
        _cancelButton.Click += (_, _) => Close();

        actionPanel.Controls.Add(_installButton);
        actionPanel.Controls.Add(_cancelButton);

        root.Controls.Add(titleLabel);
        root.Controls.Add(locationLabel);
        root.Controls.Add(locationPanel);
        root.Controls.Add(finalPathLabel);
        root.Controls.Add(_desktopShortcutCheckBox);
        root.Controls.Add(new Panel { Dock = DockStyle.Fill });
        root.Controls.Add(_progressBar);
        root.Controls.Add(_statusLabel);
        root.Controls.Add(actionPanel);

        Controls.Add(root);
    }

    private void BrowseButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = _text.BrowseDescription,
            SelectedPath = Directory.Exists(_installParentTextBox.Text)
                ? _installParentTextBox.Text
                : GetDefaultInstallParent(),
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _installParentTextBox.Text = dialog.SelectedPath;
        }
    }

    private async void InstallButton_Click(object? sender, EventArgs e)
    {
        SetBusy(true);

        try
        {
            var installParent = _installParentTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(installParent))
            {
                MessageBox.Show(this, _text.EmptyInstallPath, _text.WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var installPath = Path.Combine(installParent, AppFolderName);
            await Task.Run(() => InstallTo(installPath));
            CreateUninstallScript(installPath);

            if (_desktopShortcutCheckBox.Checked)
            {
                CreateDesktopShortcut(installPath);
            }

            RegisterUninstallEntry(installPath);
            _statusLabel.Text = _text.InstallComplete;

            var launch = MessageBox.Show(
                this,
                _text.InstallCompletePrompt,
                _text.WindowTitle,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (launch == DialogResult.Yes)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(installPath, AppExeName),
                    WorkingDirectory = installPath,
                    UseShellExecute = true
                });
            }

            Close();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = _text.InstallFailed;
            MessageBox.Show(this, ex.Message, _text.InstallFailed, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void InstallTo(string installPath)
    {
        StopRunningApp();
        Directory.CreateDirectory(installPath);
        var tempZip = Path.Combine(Path.GetTempPath(), $"LoLChatTranslator_{Guid.NewGuid():N}.zip");

        try
        {
            using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName)
                ?? throw new InvalidOperationException(_text.PayloadMissing);
            using (var file = File.Create(tempZip))
            {
                payload.CopyTo(file);
            }

            ZipFile.ExtractToDirectory(tempZip, installPath, overwriteFiles: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempZip))
                {
                    File.Delete(tempZip);
                }
            }
            catch
            {
                // A stale temp file should not make an otherwise successful install fail.
            }
        }
    }

    private static void StopRunningApp()
    {
        foreach (var process in Process.GetProcessesByName("LoLChatTranslator"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch
            {
                // Best effort: extraction will surface a clear error if files remain locked.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private void CreateDesktopShortcut(string installPath)
    {
        var shortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"{AppDisplayName}.lnk");
        var targetPath = Path.Combine(installPath, AppExeName);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException(_text.ShortcutFailed);
        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException(_text.ShortcutFailed);
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = installPath;
        shortcut.IconLocation = targetPath;
        shortcut.Description = AppDisplayName;
        shortcut.Save();

        Marshal.FinalReleaseComObject(shortcut);
        Marshal.FinalReleaseComObject(shell);
    }

    private static void RegisterUninstallEntry(string installPath)
    {
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = registry.CreateSubKey(UninstallRegistrySubKey);
        if (key is null)
        {
            throw new InvalidOperationException("Failed to create Windows uninstall registry entry.");
        }

        var appExePath = Path.Combine(installPath, AppExeName);
        var uninstallScriptPath = Path.Combine(installPath, UninstallScriptName);
        var uninstallCommand = BuildPowerShellCommand(uninstallScriptPath, quiet: false);
        var quietUninstallCommand = BuildPowerShellCommand(uninstallScriptPath, quiet: true);

        key.SetValue("DisplayName", AppDisplayName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", AppVersion, RegistryValueKind.String);
        key.SetValue("Publisher", AppPublisher, RegistryValueKind.String);
        key.SetValue("InstallLocation", installPath, RegistryValueKind.String);
        key.SetValue("DisplayIcon", $"{appExePath},0", RegistryValueKind.String);
        key.SetValue("UninstallString", uninstallCommand, RegistryValueKind.String);
        key.SetValue("QuietUninstallString", quietUninstallCommand, RegistryValueKind.String);
        key.SetValue("URLInfoAbout", AppProjectUrl, RegistryValueKind.String);
        key.SetValue("HelpLink", AppProjectUrl, RegistryValueKind.String);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", EstimateInstalledSizeKb(installPath), RegistryValueKind.DWord);
    }

    private static void CreateUninstallScript(string installPath)
    {
        var scriptPath = Path.Combine(installPath, UninstallScriptName);
        File.WriteAllText(scriptPath, BuildUninstallScript(installPath));
    }

    private static string BuildPowerShellCommand(string scriptPath, bool quiet)
    {
        var command = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"";
        return quiet ? $"{command} -Quiet" : command;
    }

    private static string BuildUninstallScript(string installPath)
    {
        var escapedRegistrySubKey = EscapePowerShellSingleQuotedString($@"HKLM:\{UninstallRegistrySubKey}");
        var escapedShortcutName = EscapePowerShellSingleQuotedString($"{AppDisplayName}.lnk");
        var escapedProductName = EscapePowerShellSingleQuotedString(AppDisplayName);
        var escapedAppExeName = EscapePowerShellSingleQuotedString(AppExeName);

        return $$"""
        param(
            [switch]$Quiet,
            [string]$OriginalScriptDirectory
        )

        $ErrorActionPreference = 'Stop'
        $uninstallKey = '{{escapedRegistrySubKey}}'
        $shortcutName = '{{escapedShortcutName}}'
        $productName = '{{escapedProductName}}'
        $appExeName = '{{escapedAppExeName}}'
        $tempScript = Join-Path $env:TEMP 'LoLChatTranslator-Uninstall.ps1'

        function ConvertTo-QuotedArgument {
            param([Parameter(Mandatory=$true)][string]$Value)
            return '"' + ($Value -replace '"', '\"') + '"'
        }

        function Get-RegistryInstallPath {
            try {
                $item = Get-ItemProperty -LiteralPath $uninstallKey -Name InstallLocation -ErrorAction Stop
                if ($item.InstallLocation -and (Test-Path -LiteralPath $item.InstallLocation -PathType Container)) {
                    return $item.InstallLocation
                }
            }
            catch {
            }

            return $null
        }

        function Get-ScriptInstallPath {
            if ($OriginalScriptDirectory -and (Test-Path -LiteralPath $OriginalScriptDirectory -PathType Container)) {
                return $OriginalScriptDirectory
            }

            if ($PSCommandPath) {
                $scriptDirectory = Split-Path -Parent $PSCommandPath
                if ($scriptDirectory -and (Test-Path -LiteralPath $scriptDirectory -PathType Container)) {
                    return $scriptDirectory
                }
            }

            return $null
        }

        function Resolve-InstallPath {
            $registryInstallPath = Get-RegistryInstallPath
            if ($registryInstallPath) {
                return $registryInstallPath
            }

            return Get-ScriptInstallPath
        }

        if ($PSCommandPath -and ((Split-Path -Parent $PSCommandPath) -ne $env:TEMP)) {
            $currentScriptDirectory = Split-Path -Parent $PSCommandPath
            Copy-Item -LiteralPath $PSCommandPath -Destination $tempScript -Force
            $arguments = '-NoProfile -ExecutionPolicy Bypass -File ' + (ConvertTo-QuotedArgument $tempScript)
            $arguments += ' -OriginalScriptDirectory ' + (ConvertTo-QuotedArgument $currentScriptDirectory)
            if ($Quiet) {
                $arguments += ' -Quiet'
            }

            $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Wait -PassThru
            exit $process.ExitCode
        }

        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        if (-not $isAdmin) {
            $arguments = '-NoProfile -ExecutionPolicy Bypass -File ' + (ConvertTo-QuotedArgument $PSCommandPath)
            if ($Quiet) {
                $arguments += ' -Quiet'
            }

            $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait -PassThru
            exit $process.ExitCode
        }

        if (-not $Quiet) {
            Add-Type -AssemblyName System.Windows.Forms
            $result = [System.Windows.Forms.MessageBox]::Show(
                "Uninstall $productName?",
                $productName,
                [System.Windows.Forms.MessageBoxButtons]::YesNo,
                [System.Windows.Forms.MessageBoxIcon]::Question)

            if ($result -ne [System.Windows.Forms.DialogResult]::Yes) {
                exit 0
            }
        }

        Get-Process -Name 'LoLChatTranslator' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

        $installPath = Resolve-InstallPath
        if (-not $installPath) {
            throw 'Cannot determine install location. Registry InstallLocation is missing and script directory is unavailable.'
        }

        $appExePath = Join-Path $installPath $appExeName
        if (-not (Test-Path -LiteralPath $appExePath -PathType Leaf)) {
            throw "Refusing to delete '$installPath' because '$appExeName' was not found inside it."
        }

        $desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) $shortcutName
        if (Test-Path -LiteralPath $desktopShortcut) {
            Remove-Item -LiteralPath $desktopShortcut -Force -ErrorAction SilentlyContinue
        }

        if (Test-Path -LiteralPath $uninstallKey) {
            Remove-Item -LiteralPath $uninstallKey -Recurse -Force -ErrorAction SilentlyContinue
        }

        Start-Sleep -Milliseconds 500

        Remove-Item -LiteralPath $installPath -Recurse -Force

        exit 0
        """;
    }

    private static int EstimateInstalledSizeKb(string installPath)
    {
        try
        {
            var bytes = Directory.EnumerateFiles(installPath, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
            return Math.Max(1, (int)Math.Ceiling(bytes / 1024D));
        }
        catch
        {
            return 1;
        }
    }

    private static string EscapePowerShellSingleQuotedString(string value)
    {
        return value.Replace("'", "''");
    }

    private void SetBusy(bool busy)
    {
        _installButton.Enabled = !busy;
        _cancelButton.Enabled = !busy;
        _browseButton.Enabled = !busy;
        _installParentTextBox.Enabled = !busy;
        _desktopShortcutCheckBox.Enabled = !busy;
        _progressBar.Visible = busy;
        _progressBar.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        _statusLabel.Text = busy ? _text.Installing : string.Empty;
    }

    private static string GetDefaultInstallParent()
    {
        var existingInstallPath = TryReadExistingInstallLocation();
        if (!string.IsNullOrWhiteSpace(existingInstallPath))
        {
            var parent = Directory.GetParent(existingInstallPath);
            if (parent is not null)
            {
                return parent.FullName;
            }
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    }

    private static string? TryReadExistingInstallLocation()
    {
        try
        {
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = registry.OpenSubKey(UninstallRegistrySubKey);
            return key?.GetValue("InstallLocation") as string;
        }
        catch
        {
            return null;
        }
    }

    private static Icon? LoadInstallerIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
        return File.Exists(iconPath) ? new Icon(iconPath) : null;
    }
}

internal sealed record InstallerText(
    string WindowTitle,
    string Title,
    string InstallLocationLabel,
    string FinalPathHint,
    string Browse,
    string BrowseDescription,
    string CreateDesktopShortcut,
    string Install,
    string Cancel,
    string Installing,
    string InstallComplete,
    string InstallCompletePrompt,
    string InstallFailed,
    string EmptyInstallPath,
    string PayloadMissing,
    string ShortcutFailed)
{
    public static InstallerText ForCurrentCulture()
    {
        var culture = CultureInfo.CurrentUICulture.Name;

        if (culture.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)
            || culture.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase)
            || culture.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase))
        {
            return new InstallerText(
                "LOL Chat OCR Translator 安裝程式",
                "安裝 LOL Chat OCR Translator",
                "安裝位置（會在此位置建立 LoLChatTranslator 資料夾）",
                "最終安裝路徑：所選位置\\LoLChatTranslator",
                "瀏覽...",
                "選擇安裝父資料夾",
                "在桌面建立捷徑",
                "安裝",
                "取消",
                "正在安裝...",
                "安裝完成。",
                "安裝完成。是否立即啟動程式？",
                "安裝失敗",
                "請選擇安裝位置。",
                "安裝包內容遺失。",
                "建立桌面捷徑失敗。");
        }

        if (culture.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
        {
            return new InstallerText(
                "LOL Chat OCR Translator 설치 프로그램",
                "LOL Chat OCR Translator 설치",
                "설치 위치(이 위치 아래 LoLChatTranslator 폴더가 생성됩니다)",
                "최종 설치 경로: 선택한 위치\\LoLChatTranslator",
                "찾아보기...",
                "설치할 상위 폴더 선택",
                "바탕화면 바로가기 만들기",
                "설치",
                "취소",
                "설치 중...",
                "설치가 완료되었습니다.",
                "설치가 완료되었습니다. 지금 실행할까요?",
                "설치 실패",
                "설치 위치를 선택하세요.",
                "설치 패키지 내용이 없습니다.",
                "바탕화면 바로가기 만들기에 실패했습니다.");
        }

        if (culture.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return new InstallerText(
                "LOL Chat OCR Translator インストーラー",
                "LOL Chat OCR Translator をインストール",
                "インストール先（ここに LoLChatTranslator フォルダーを作成します）",
                "最終インストール先: 選択した場所\\LoLChatTranslator",
                "参照...",
                "インストール先の親フォルダーを選択",
                "デスクトップにショートカットを作成",
                "インストール",
                "キャンセル",
                "インストール中...",
                "インストールが完了しました。",
                "インストールが完了しました。今すぐ起動しますか？",
                "インストール失敗",
                "インストール先を選択してください。",
                "インストールパッケージの内容が見つかりません。",
                "デスクトップショートカットの作成に失敗しました。");
        }

        if (culture.StartsWith("vi", StringComparison.OrdinalIgnoreCase))
        {
            return new InstallerText(
                "Trình cài đặt LOL Chat OCR Translator",
                "Cài đặt LOL Chat OCR Translator",
                "Vị trí cài đặt (sẽ tạo thư mục LoLChatTranslator tại đây)",
                "Đường dẫn cài đặt cuối cùng: vị trí đã chọn\\LoLChatTranslator",
                "Duyệt...",
                "Chọn thư mục cha để cài đặt",
                "Tạo lối tắt trên màn hình nền",
                "Cài đặt",
                "Hủy",
                "Đang cài đặt...",
                "Cài đặt hoàn tất.",
                "Cài đặt hoàn tất. Khởi chạy ngay bây giờ?",
                "Cài đặt thất bại",
                "Vui lòng chọn vị trí cài đặt.",
                "Thiếu nội dung gói cài đặt.",
                "Không thể tạo lối tắt trên màn hình nền.");
        }

        if (culture.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return new InstallerText(
                "LOL Chat OCR Translator Setup",
                "Install LOL Chat OCR Translator",
                "Install location (LoLChatTranslator will be created inside this folder)",
                "Final install path: selected location\\LoLChatTranslator",
                "Browse...",
                "Choose the parent install folder",
                "Create desktop shortcut",
                "Install",
                "Cancel",
                "Installing...",
                "Installation complete.",
                "Installation complete. Launch the app now?",
                "Installation failed",
                "Please choose an install location.",
                "Installer payload is missing.",
                "Failed to create the desktop shortcut.");
        }

        return new InstallerText(
            "LOL Chat OCR Translator 安装程序",
            "安装 LOL Chat OCR Translator",
            "安装位置（将在此位置新建 LoLChatTranslator 文件夹）",
            "最终安装路径：所选位置\\LoLChatTranslator",
            "浏览...",
            "选择安装父文件夹",
            "在桌面新建快捷方式",
            "安装",
            "取消",
            "正在安装...",
            "安装完成。",
            "安装完成。是否立即启动程序？",
            "安装失败",
            "请选择安装位置。",
            "安装包内容缺失。",
            "创建桌面快捷方式失败。");
    }
}
