using System.Runtime.InteropServices;

namespace LoLChatTranslator.Services;

public static class DpiAwarenessService
{
    private const int ProcessPerMonitorDpiAware = 2;
    private const int HResultAccessDenied = unchecked((int)0x80070005);

    public static void EnableProcessDpiAwareness()
    {
        try
        {
            var result = SetProcessDpiAwareness(ProcessPerMonitorDpiAware);
            if (result == 0 || result == HResultAccessDenied)
            {
                return;
            }
        }
        catch
        {
            // Fall back to older system-DPI awareness below.
        }

        try
        {
            SetProcessDPIAware();
        }
        catch
        {
            // WPF can still run if Windows rejects the DPI awareness request.
        }
    }

    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int value);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
}
