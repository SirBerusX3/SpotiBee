using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpotiBee.Spotify
{
    /// <summary>
    /// Thin wrapper over the Spotify Web API. Handles access-token refresh,
    /// one retry on 401, and short Retry-After waits on 429.
    /// </summary>
    public sealed class SpotifyClient : ISpotifyPlaylists, IDisposable
    {
        private const string ApiBase = "https://api.spotify.com/v1/";
        private static readonly TimeSpan MaxInlineRetryWait = TimeSpan.FromSeconds(5);

        private readonly HttpClient http;
        private readonly string clientId;
        private readonly SemaphoreSlim tokenLock = new SemaphoreSlim(1, 1);
        private string accessToken;
        private DateTime accessTokenExpiresUtc;
        private string refreshToken;

        public SpotifyClient(HttpClient http, string clientId, string refreshToken)
        {
            this.http = http;
            this.clientId = clientId;
            this.refreshToken = refreshToken;
        }

        public SpotifyClient(HttpClient http, string clientId, TokenResponse token)
            : this(http, clientId, token.RefreshToken)
        {
            ApplyToken(token);
        }

        /// <summary>Raised when Spotify rotates the refresh token, so it can be persisted.</summary>
        public event Action<string> RefreshTokenChanged;

        public string RefreshToken => refreshToken;

        // --- Profile -------------------------------------------------------

        public Task<UserProfile> GetCurrentUserAsync(CancellationToken ct = default) =>
            GetAsync<UserProfile>("me", ct);

        // --- Player --------------------------------------------------------

        /// <summary>Returns null when nothing is playing on any device (HTTP 204).</summary>
        public Task<PlaybackState> GetPlaybackStateAsync(CancellationToken ct = default) =>
            GetAsync<PlaybackState>("me/player?additional_types=episode", ct);

        public async Task<Device[]> GetDevicesAsync(CancellationToken ct = default) =>
            (await GetAsync<DeviceList>("me/player/devices", ct).ConfigureAwait(false))?.Devices ?? new Device[0];

        public Task PlayAsync(string deviceId = null) =>
            SendAsync(HttpMethod.Put, WithDevice("me/player/play", deviceId), null);

        public Task PlayUrisAsync(IEnumerable<string> uris, string deviceId = null, int? positionMs = null) =>
            SendAsync(HttpMethod.Put, WithDevice("me/player/play", deviceId),
                Json.Stringify(new PlayRequest { Uris = uris.ToArray(), PositionMs = positionMs }));

        public Task PauseAsync(string deviceId = null) =>
            SendAsync(HttpMethod.Put, WithDevice("me/player/pause", deviceId), null);

        public Task NextAsync(string deviceId = null) =>
            SendAsync(HttpMethod.Post, WithDevice("me/player/next", deviceId), null);

        public Task PreviousAsync(string deviceId = null) =>
            SendAsync(HttpMethod.Post, WithDevice("me/player/previous", deviceId), null);

        public Task SeekAsync(int positionMs, string deviceId = null) =>
            SendAsync(HttpMethod.Put, WithDevice($"me/player/seek?position_ms={Math.Max(0, positionMs)}", deviceId), null);

        public Task SetVolumeAsync(int percent, string deviceId = null) =>
            SendAsync(HttpMethod.Put, WithDevice($"me/player/volume?volume_percent={Math.Max(0, Math.Min(100, percent))}", deviceId), null);

        public Task SetShuffleAsync(bool enabled, string deviceId = null) =>
            SendAsync(HttpMethod.Put, WithDevice($"me/player/shuffle?state={(enabled ? "true" : "false")}", deviceId), null);

        /// <param name="state">"track", "context" or "off"</param>
        public Task SetRepeatAsync(string state, string deviceId = null) =>
            SendAsync(HttpMethod.Put, WithDevice($"me/player/repeat?state={state}", deviceId), null);

        public Task TransferPlaybackAsync(string deviceId, bool play) =>
            SendAsync(HttpMethod.Put, "me/player",
                Json.Stringify(new TransferRequest { DeviceIds = new[] { deviceId }, Play = play }));

        // --- Playlists and library ----------------------------------------

        public Task<Playlist[]> GetMyPlaylistsAsync(CancellationToken ct = default) =>
            GetAllPagesAsync<Playlist>("me/playlists?limit=50", ct);

        public Task<Playlist> GetPlaylistAsync(string playlistId, CancellationToken ct = default) =>
            GetAsync<Playlist>($"playlists/{Uri.EscapeDataString(playlistId)}?fields=id,name,uri,snapshot_id,owner(id,display_name)", ct);

        /// <summary>Only works for playlists the user owns or collaborates on (Spotify returns 403 otherwise).</summary>
        public Task<PlaylistItem[]> GetPlaylistItemsAsync(string playlistId, IProgress<int> progress = null, CancellationToken ct = default) =>
            GetAllPagesAsync<PlaylistItem>($"playlists/{Uri.EscapeDataString(playlistId)}/items?limit=50", ct, progress);

        public Task<Paging<SavedTrack>> GetSavedTracksPageAsync(int limit, CancellationToken ct = default) =>
            GetAsync<Paging<SavedTrack>>($"me/tracks?limit={Math.Max(1, Math.Min(50, limit))}", ct);

        public Task<SavedTrack[]> GetSavedTracksAsync(IProgress<int> progress = null, CancellationToken ct = default) =>
            GetAllPagesAsync<SavedTrack>("me/tracks?limit=50", ct, progress);

        public const int MaxPlaylistItemsPerRequest = 100;
        public const int MaxLibraryItemsPerRequest = 40;
        public const int MaxSearchResults = 10;

        public async Task<Playlist> CreatePlaylistAsync(string name, string description, CancellationToken ct = default)
        {
            var body = await SendAsync(HttpMethod.Post, "me/playlists",
                Json.Stringify(new CreatePlaylistRequest { Name = name, Public = false, Description = description }), ct).ConfigureAwait(false);
            return Json.Parse<Playlist>(body);
        }

        /// <summary>Inserts up to 100 URIs at a position (or appends). Returns the new snapshot ID.</summary>
        public async Task<string> AddPlaylistItemsAsync(string playlistId, IList<string> uris, int? position, CancellationToken ct = default)
        {
            var body = await SendAsync(HttpMethod.Post, $"playlists/{Uri.EscapeDataString(playlistId)}/items",
                Json.Stringify(new PlaylistItemsRequest { Uris = uris.ToArray(), Position = position }), ct).ConfigureAwait(false);
            return ParseSnapshot(body);
        }

        /// <summary>Removes every occurrence of up to 100 URIs. Returns the new snapshot ID.</summary>
        public async Task<string> RemovePlaylistItemsAsync(string playlistId, IList<string> uris, CancellationToken ct = default)
        {
            var body = await SendAsync(HttpMethod.Delete, $"playlists/{Uri.EscapeDataString(playlistId)}/items",
                Json.Stringify(new RemoveItemsRequest { Items = uris.Select(u => new UriRef { Uri = u }).ToArray() }), ct).ConfigureAwait(false);
            return ParseSnapshot(body);
        }

        /// <summary>Sets the playlist to exactly these URIs, in order (any length). Returns the new snapshot ID.</summary>
        public async Task<string> ReplacePlaylistItemsAsync(string playlistId, IList<string> uris, CancellationToken ct = default)
        {
            var first = uris.Take(MaxPlaylistItemsPerRequest).ToArray();
            var body = await SendAsync(HttpMethod.Put, $"playlists/{Uri.EscapeDataString(playlistId)}/items",
                Json.Stringify(new PlaylistItemsRequest { Uris = first }), ct).ConfigureAwait(false);
            var snapshot = ParseSnapshot(body);
            for (var i = MaxPlaylistItemsPerRequest; i < uris.Count; i += MaxPlaylistItemsPerRequest)
                snapshot = await AddPlaylistItemsAsync(playlistId, uris.Skip(i).Take(MaxPlaylistItemsPerRequest).ToList(), null, ct).ConfigureAwait(false);
            return snapshot;
        }

        public async Task<Track[]> SearchTracksAsync(string query, int limit = MaxSearchResults, CancellationToken ct = default)
        {
            var path = $"search?type=track&limit={Math.Min(limit, MaxSearchResults)}&market=from_token&q={Uri.EscapeDataString(query)}";
            var result = await GetAsync<SearchResponse>(path, ct).ConfigureAwait(false);
            return result?.Tracks?.Items?.Where(t => t != null).ToArray() ?? new Track[0];
        }

        /// <summary>Adds tracks to Liked Songs.</summary>
        public Task SaveToLibraryAsync(IEnumerable<string> uris, CancellationToken ct = default) =>
            LibraryAsync(HttpMethod.Put, uris, ct);

        /// <summary>Removes tracks from Liked Songs.</summary>
        public Task RemoveFromLibraryAsync(IEnumerable<string> uris, CancellationToken ct = default) =>
            LibraryAsync(HttpMethod.Delete, uris, ct);

        private async Task LibraryAsync(HttpMethod method, IEnumerable<string> uris, CancellationToken ct)
        {
            var list = uris.ToList();
            for (var i = 0; i < list.Count; i += MaxLibraryItemsPerRequest)
            {
                var chunk = string.Join(",", list.Skip(i).Take(MaxLibraryItemsPerRequest));
                await SendAsync(method, "me/library?uris=" + Uri.EscapeDataString(chunk), null, ct).ConfigureAwait(false);
            }
        }

        private static string ParseSnapshot(string body) =>
            Json.TryParse<SnapshotResponse>(body, out var s) ? s.SnapshotId : null;

        /// <summary>Follows "next" links until every page is fetched. Progress reports the running item count.</summary>
        private async Task<T[]> GetAllPagesAsync<T>(string firstPath, CancellationToken ct, IProgress<int> progress = null)
        {
            var all = new List<T>();
            var path = firstPath;
            while (path != null)
            {
                ct.ThrowIfCancellationRequested();
                var page = await GetAsync<Paging<T>>(path, ct).ConfigureAwait(false);
                if (page?.Items != null)
                    all.AddRange(page.Items);
                progress?.Report(all.Count);
                path = page?.Next != null && page.Next.StartsWith(ApiBase, StringComparison.Ordinal)
                    ? page.Next.Substring(ApiBase.Length)
                    : null;
            }
            return all.ToArray();
        }

        // --- Plumbing ------------------------------------------------------

        private async Task<T> GetAsync<T>(string path, CancellationToken ct) where T : class
        {
            var body = await SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body) ? null : Json.Parse<T>(body);
        }

        private async Task<string> SendAsync(HttpMethod method, string path, string jsonBody, CancellationToken ct = default)
        {
            var retriedAuth = false;
            var retriedRateLimit = false;

            while (true)
            {
                var token = await GetAccessTokenAsync(forceRefresh: false).ConfigureAwait(false);
                using var request = new HttpRequestMessage(method, ApiBase + path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (jsonBody != null)
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                else if (method != HttpMethod.Get)
                    // Spotify rejects some body-less PUT/POSTs without Content-Length
                    request.Content = new StringContent("", Encoding.UTF8, "application/json");

                using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                var body = response.Content == null ? "" : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    return body;

                if (response.StatusCode == HttpStatusCode.Unauthorized && !retriedAuth)
                {
                    retriedAuth = true;
                    await GetAccessTokenAsync(forceRefresh: true).ConfigureAwait(false);
                    continue;
                }

                if ((int)response.StatusCode == 429)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                    if (!retriedRateLimit && wait <= MaxInlineRetryWait)
                    {
                        retriedRateLimit = true;
                        await Task.Delay(wait, ct).ConfigureAwait(false);
                        continue;
                    }
                    throw new SpotifyApiException(response.StatusCode, "Rate limited by Spotify", "RATE_LIMITED", wait);
                }

                Json.TryParse<ApiErrorEnvelope>(body, out var envelope);
                throw new SpotifyApiException(response.StatusCode,
                    envelope?.Error?.Message ?? $"Spotify returned {(int)response.StatusCode} {response.ReasonPhrase}",
                    envelope?.Error?.Reason);
            }
        }

        private async Task<string> GetAccessTokenAsync(bool forceRefresh)
        {
            await tokenLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!forceRefresh && accessToken != null && DateTime.UtcNow < accessTokenExpiresUtc)
                    return accessToken;
                if (string.IsNullOrEmpty(refreshToken))
                    throw new SpotifyAuthException("Not connected to Spotify.", isInvalidGrant: true);

                var token = await SpotifyAuth.RefreshAsync(http, clientId, refreshToken).ConfigureAwait(false);
                ApplyToken(token);
                return accessToken;
            }
            finally
            {
                tokenLock.Release();
            }
        }

        private void ApplyToken(TokenResponse token)
        {
            accessToken = token.AccessToken;
            // Refresh a minute early to avoid racing expiry mid-request
            accessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn) - 60);
            if (!string.IsNullOrEmpty(token.RefreshToken) && token.RefreshToken != refreshToken)
            {
                refreshToken = token.RefreshToken;
                RefreshTokenChanged?.Invoke(refreshToken);
            }
        }

        private static string WithDevice(string path, string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
                return path;
            return path + (path.Contains("?") ? "&" : "?") + "device_id=" + Uri.EscapeDataString(deviceId);
        }

        public void Dispose() => tokenLock.Dispose();
    }

    public class SpotifyApiException : Exception
    {
        public SpotifyApiException(HttpStatusCode statusCode, string message, string reason, TimeSpan? retryAfter = null)
            : base(message)
        {
            StatusCode = statusCode;
            Reason = reason;
            RetryAfter = retryAfter;
        }

        public HttpStatusCode StatusCode { get; }

        /// <summary>Spotify's machine-readable reason, e.g. PREMIUM_REQUIRED or NO_ACTIVE_DEVICE.</summary>
        public string Reason { get; }

        public TimeSpan? RetryAfter { get; }

        public bool IsNoActiveDevice => Reason == "NO_ACTIVE_DEVICE" || StatusCode == HttpStatusCode.NotFound;

        public string FriendlyMessage
        {
            get
            {
                if (Reason == "PREMIUM_REQUIRED")
                    return "Spotify Premium is required to control playback.";
                if (IsNoActiveDevice)
                    return "No active Spotify device. Open Spotify or pick a device.";
                if (Reason == "RATE_LIMITED")
                    return "Spotify is rate limiting requests. Slowing down…";
                if (StatusCode == HttpStatusCode.Forbidden)
                    return "Spotify refused that action: " + Message;
                return Message;
            }
        }
    }
}

namespace SpotiBee.Spotify
{
    /// <summary>The playlist and library operations used by import and sync, so they can be tested offline.</summary>
    public interface ISpotifyPlaylists : SpotiBee.Library.ISpotifySearch
    {
        Task<Playlist> GetPlaylistAsync(string playlistId, CancellationToken ct = default);
        Task<Playlist[]> GetMyPlaylistsAsync(CancellationToken ct = default);
        Task<PlaylistItem[]> GetPlaylistItemsAsync(string playlistId, IProgress<int> progress = null, CancellationToken ct = default);
        Task<SavedTrack[]> GetSavedTracksAsync(IProgress<int> progress = null, CancellationToken ct = default);
        Task<Paging<SavedTrack>> GetSavedTracksPageAsync(int limit, CancellationToken ct = default);
        Task<Playlist> CreatePlaylistAsync(string name, string description, CancellationToken ct = default);
        Task<string> AddPlaylistItemsAsync(string playlistId, IList<string> uris, int? position, CancellationToken ct = default);
        Task<string> RemovePlaylistItemsAsync(string playlistId, IList<string> uris, CancellationToken ct = default);
        Task<string> ReplacePlaylistItemsAsync(string playlistId, IList<string> uris, CancellationToken ct = default);
        Task SaveToLibraryAsync(IEnumerable<string> uris, CancellationToken ct = default);
        Task RemoveFromLibraryAsync(IEnumerable<string> uris, CancellationToken ct = default);
    }
}
