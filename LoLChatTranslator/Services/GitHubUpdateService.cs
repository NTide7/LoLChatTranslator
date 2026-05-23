using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LoLChatTranslator.Services;

public sealed class GitHubUpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/NTide7/LoLChatTranslator/releases/latest";
    private const string ReleasesPageUrl = "https://github.com/NTide7/LoLChatTranslator/releases";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task<UpdateCheckResult> CheckLatestReleaseAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await HttpClient.GetFromJsonAsync<GitHubRelease>(LatestReleaseUrl, cancellationToken);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return UpdateCheckResult.Failed("未能读取 Github 最新版本信息。");
            }

            if (!TryParseVersion(release.TagName, out var latestVersion))
            {
                return UpdateCheckResult.Failed($"Github 最新版本号无效：{release.TagName}");
            }

            var downloadUrl = string.IsNullOrWhiteSpace(release.HtmlUrl)
                ? ReleasesPageUrl
                : release.HtmlUrl;

            return ToComparableVersion(latestVersion).CompareTo(ToComparableVersion(currentVersion)) > 0
                ? UpdateCheckResult.UpdateAvailable(latestVersion, downloadUrl)
                : UpdateCheckResult.UpToDate(latestVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return UpdateCheckResult.Failed($"检查更新失败：{ex.Message}");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("LOL-Chat-OCR-Translator/1.0.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var metadataIndex = normalized.IndexOfAny(['-', '+']);
        if (metadataIndex >= 0)
        {
            normalized = normalized[..metadataIndex];
        }

        return Version.TryParse(normalized, out version!);
    }

    private static Version ToComparableVersion(Version version)
    {
        return new Version(
            version.Major,
            version.Minor,
            Math.Max(0, version.Build),
            Math.Max(0, version.Revision));
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }
}

public sealed record UpdateCheckResult(
    bool HasUpdate,
    Version? LatestVersion,
    string? DownloadUrl,
    string? ErrorMessage)
{
    public static UpdateCheckResult UpdateAvailable(Version latestVersion, string downloadUrl)
    {
        return new UpdateCheckResult(true, latestVersion, downloadUrl, null);
    }

    public static UpdateCheckResult UpToDate(Version latestVersion)
    {
        return new UpdateCheckResult(false, latestVersion, null, null);
    }

    public static UpdateCheckResult Failed(string errorMessage)
    {
        return new UpdateCheckResult(false, null, null, errorMessage);
    }
}
