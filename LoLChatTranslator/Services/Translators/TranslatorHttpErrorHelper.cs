using System.Net.Http;
using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

internal static class TranslatorHttpErrorHelper
{
    public static async Task<string> BuildHttpErrorAsync(
        HttpResponseMessage response,
        TranslateConfig config,
        string serviceName,
        CancellationToken cancellationToken)
    {
        var body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            // Keep the status code error even when the response body cannot be read.
        }

        var detail = string.IsNullOrWhiteSpace(body)
            ? response.ReasonPhrase
            : TranslatorErrorSanitizer.Sanitize(body, config);

        return $"[{serviceName} 翻译失败] HTTP {(int)response.StatusCode}：{detail}";
    }
}
