using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AppDock.Services;

/// <summary>
/// Optional update check against a GitHub "latest release" API URL (settings: updateFeedUrl). Off when no URL is
/// configured; never downloads or installs anything by itself.
/// </summary>
internal static class UpdateChecker
{
    public sealed record Release(string Version, string Url);

    public static async Task<Release?> CheckAsync(string? feedUrl, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(feedUrl) || !Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
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
            return System.Version.TryParse(tag, out var remote) &&
                   System.Version.TryParse(AppInfo.Version, out var local) && remote > local
                ? new Release(tag, latest.HtmlUrl)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            Log.Information("Update check failed: {Message}", ex.Message);
            return null;
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }
}
