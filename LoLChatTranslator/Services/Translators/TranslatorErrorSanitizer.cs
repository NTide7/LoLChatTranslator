using LoLChatTranslator.Models;
using System.Text.RegularExpressions;

namespace LoLChatTranslator.Services;

public static class TranslatorErrorSanitizer
{
    private const int MaxErrorLength = 500;

    public static string Sanitize(string? message, TranslateConfig? config = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "未知错误。";
        }

        var sanitized = message;
        var apiKey = config is null ? string.Empty : TranslatorCredentialStore.GetApiKey(config);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            sanitized = sanitized.Replace(apiKey, "***", StringComparison.Ordinal);
            sanitized = sanitized.Replace(Uri.EscapeDataString(apiKey), "***", StringComparison.Ordinal);
        }

        sanitized = Regex.Replace(
            sanitized,
            @"Bearer\s+[^,\s""']+",
            "Bearer ***",
            RegexOptions.IgnoreCase);

        return sanitized.Length > MaxErrorLength
            ? $"{sanitized[..MaxErrorLength]}..."
            : sanitized;
    }

    public static bool IsErrorResult(string text)
    {
        return text.TrimStart().StartsWith("[", StringComparison.Ordinal)
            && (text.Contains("失败", StringComparison.Ordinal)
                || text.Contains("错误", StringComparison.Ordinal)
                || text.Contains("超时", StringComparison.Ordinal)
                || text.Contains("未配置", StringComparison.Ordinal));
    }
}
