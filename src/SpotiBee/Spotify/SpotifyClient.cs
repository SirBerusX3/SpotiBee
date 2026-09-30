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
    public sealed class SpotifyClient : IDisposable
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
