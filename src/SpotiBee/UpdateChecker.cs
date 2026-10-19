using System;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SpotiBee
{
    public sealed class ReleaseInfo
    {
        public string Tag { get; set; }
        public Version Version { get; set; }
        public string Url { get; set; }
    }

    /// <summary>
    /// Looks for a newer SpotiBee release on GitHub, at most once a day. Only the version number
    /// (in the User-Agent) is sent. Must be used on MusicBee's UI thread, since it saves settings.
    /// </summary>
    public sealed class UpdateChecker
    {
        public const string ReleasesPage = "https://github.com/SirBerusX3/SpotiBee/releases/latest";
        private const string LatestReleaseApi = "https://api.github.com/repos/SirBerusX3/SpotiBee/releases/latest";
        private static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);

        private readonly HttpClient http;
        private readonly PluginSettings settings;

        public UpdateChecker(HttpClient http, PluginSettings settings, Version current)
        {
            this.http = http;
            this.settings = settings;
            Current = ThreePart(current);
        }

        public Version Current { get; }

        /// <summary>
        /// The newest release if it's newer than this one and the user hasn't hidden it; otherwise
        /// null. Uses the last answer if GitHub was asked within the past day.
        /// </summary>
        public async Task<ReleaseInfo> CheckAsync(CancellationToken ct = default)
        {
            if (settings.DisableUpdateCheck)
                return null;

            if (settings.LastUpdateCheckUtc == null || DateTime.UtcNow - settings.LastUpdateCheckUtc.Value >= CheckEvery)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await http.SendAsync(request, ct);
                // 404 just means nothing has been released yet
                if (response.IsSuccessStatusCode &&
                    Json.TryParse<GitHubRelease>(await response.Content.ReadAsStringAsync(), out var release) &&
                    !release.Draft && !release.Prerelease)
                {
                    settings.LatestReleaseTag = release.TagName;
                    settings.LatestReleaseUrl = release.HtmlUrl;
                }
                else if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                {
                    // Rate limited or GitHub having trouble: try again later rather than waiting a day
                    throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}");
                }
                settings.LastUpdateCheckUtc = DateTime.UtcNow;
                settings.Save();
            }

            var latest = ParseTag(settings.LatestReleaseTag);
            if (latest == null || latest <= Current || settings.LatestReleaseTag == settings.DismissedReleaseTag)
                return null;
            return new ReleaseInfo
            {
                Tag = settings.LatestReleaseTag,
                Version = latest,
                Url = string.IsNullOrEmpty(settings.LatestReleaseUrl) ? ReleasesPage : settings.LatestReleaseUrl,
            };
        }

        /// <summary>Stops mentioning this release; a later one is still announced.</summary>
        public void Dismiss(ReleaseInfo release)
        {
            settings.DismissedReleaseTag = release.Tag;
            settings.Save();
        }

        /// <summary>"v0.7.0" or "0.7.0" → 0.7.0; null if it isn't a version number.</summary>
        public static Version ParseTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return null;
            tag = tag.Trim().TrimStart('v', 'V');
            return Version.TryParse(tag, out var version) ? ThreePart(version) : null;
        }

        // The assembly says 0.6.0.0 and tags say 0.6.0; compare them as equals
        private static Version ThreePart(Version v) => new Version(v.Major, v.Minor, Math.Max(0, v.Build));

        [DataContract]
        private sealed class GitHubRelease
        {
            [DataMember(Name = "tag_name")] public string TagName { get; set; }
            [DataMember(Name = "html_url")] public string HtmlUrl { get; set; }
            [DataMember(Name = "draft")] public bool Draft { get; set; }
            [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
        }
    }
}
