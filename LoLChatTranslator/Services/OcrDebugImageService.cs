using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace LoLChatTranslator.Services;

public static class OcrDebugImageService
{
    private static readonly object SyncRoot = new();

    public static string? TrySaveOcrInputImage(Bitmap image, string fileName = "debug_ocr_input.png")
    {
        try
        {
            var directory = AppLogService.ResolveLogDirectory();
            var path = Path.Combine(directory, fileName);
            lock (SyncRoot)
            {
                image.Save(path, ImageFormat.Png);
            }

            return path;
        }
        catch
        {
            return null;
        }
    }
}
