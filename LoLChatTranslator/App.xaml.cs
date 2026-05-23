using System.Windows;
using LoLChatTranslator.Services;

namespace LoLChatTranslator;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    static App()
    {
        DpiAwarenessService.EnableProcessDpiAwareness();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (OcrSelfTestRunner.ShouldRun(e.Args))
        {
            var exitCode = await OcrSelfTestRunner.RunAsync(e.Args);
            Shutdown(exitCode);
            return;
        }

        var installOcrDependenciesOnStartup = e.Args.Any(arg =>
            arg.Equals("--install-ocr-deps", StringComparison.OrdinalIgnoreCase));
        new MainWindow(installOcrDependenciesOnStartup).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogService.FlushPending(TimeSpan.FromSeconds(1));
        base.OnExit(e);
    }
}
