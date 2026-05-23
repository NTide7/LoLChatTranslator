using LoLChatTranslator.Models;
using System.Security.Cryptography;
using System.Text;

namespace LoLChatTranslator.Services;

public static class TranslatorCredentialStore
{
    private const string DpapiPrefix = "dpapi:";

    public static string GetApiKey(TranslateConfig config)
    {
        var storedValue = config.ApiKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return string.Empty;
        }

        if (!storedValue.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            return storedValue;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(storedValue[DpapiPrefix.Length..]);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    public static void SetApiKey(TranslateConfig config, string apiKey)
    {
        config.ApiKey = string.IsNullOrWhiteSpace(apiKey)
            ? string.Empty
            : Protect(apiKey.Trim());
    }

    public static bool ProtectApiKeyInConfig(TranslateConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey)
            || config.ApiKey.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        config.ApiKey = Protect(config.ApiKey.Trim());
        return true;
    }

    private static string Protect(string apiKey)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(apiKey);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return $"{DpapiPrefix}{Convert.ToBase64String(protectedBytes)}";
        }
        catch
        {
            return string.Empty;
        }
    }
}
