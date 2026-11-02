using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AppDock.Core.Updates;
using Serilog;

namespace AppDock.Services;

/// <summary>
/// Optional update check against a GitHub "latest release" API URL (settings: updateFeedUrl). Off when no URL is
/// configured. Only reads the release; <see cref="AutoUpdater"/> does the downloading and installing.
/// </summary>
internal static class UpdateChecker
{
    /// <summary>A newer release. The installer fields are empty if the release has no usable installer.</summary>
    public sealed record Release(string Version, string Url, Uri? InstallerUrl, string? InstallerSha256, long InstallerSize);

    public static bool IsConfigured(string? feedUrl) => FeedUri(feedUrl) is not null;

    public static async Task<Release?> CheckAsync(string? feedUrl, CancellationToken token)
    {
        var uri = FeedUri(feedUrl);
        if (uri is null)
        {
            return null;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version}");
            var latest = await http.GetFromJsonAsync<GitHubRelease>(uri, token).ConfigureAwait(false);
            if (latest?.TagName is null || latest.HtmlUrl is null)
            {
                return null;
            }

            var tag = latest.TagName.TrimStart('v', 'V');
            if (UpdatePolicy.Classify(AppInfo.Version, tag) == UpdateAction.None)
            {
                return null;
            }

            var asset = latest.Assets?.FirstOrDefault(a => string.Equals(a.Name, UpdatePolicy.AssetName(tag), StringComparison.OrdinalIgnoreCase));
            var installerUrl = Uri.TryCreate(asset?.DownloadUrl, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps ? parsed : null;
            return new Release(tag, latest.HtmlUrl, installerUrl, UpdatePolicy.ParseSha256Digest(asset?.Digest), asset?.Size ?? 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            Log.Information("Update check failed: {Message}", ex.Message);
            return null;
        }
    }

    private static Uri? FeedUri(string? feedUrl) =>
        !string.IsNullOrWhiteSpace(feedUrl) && Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
