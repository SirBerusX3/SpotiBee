using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpotiBee.Spotify
{
    /// <summary>
    /// Authorization Code flow with PKCE, so no client secret is stored in the plugin.
    /// The browser redirects back to a tiny loopback listener on 127.0.0.1.
    /// </summary>
    public static class SpotifyAuth
    {
        public const int CallbackPort = 5543;
        public static readonly string RedirectUri = $"http://127.0.0.1:{CallbackPort}/callback";

        private const string AuthorizeEndpoint = "https://accounts.spotify.com/authorize";
        private const string TokenEndpoint = "https://accounts.spotify.com/api/token";

        public static readonly string[] Scopes =
        {
            "user-read-private",
            "user-read-playback-state",
            "user-modify-playback-state",
            "user-read-currently-playing",
            "playlist-read-private",
            "playlist-read-collaborative",
            "playlist-modify-private",
            "playlist-modify-public",
            "user-library-read",
            "user-library-modify",
        };

        public static async Task<TokenResponse> AuthorizeAsync(HttpClient http, string clientId, CancellationToken cancellationToken)
        {
            var verifier = Base64Url(RandomBytes(64));
            var challenge = Base64Url(SHA256.Create().ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            var state = Base64Url(RandomBytes(16));

            var url = AuthorizeEndpoint + "?" + FormEncode(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["response_type"] = "code",
                ["redirect_uri"] = RedirectUri,
                ["code_challenge_method"] = "S256",
                ["code_challenge"] = challenge,
                ["state"] = state,
                ["scope"] = string.Join(" ", Scopes),
            });

            var listener = new TcpListener(IPAddress.Loopback, CallbackPort);
            try
            {
                listener.Start();
            }
            catch (SocketException ex)
            {
                throw new SpotifyAuthException($"Couldn't listen on port {CallbackPort} for the Spotify login callback ({ex.Message}). Is another app using it?");
            }

            string code;
            try
            {
                using (cancellationToken.Register(() => listener.Stop()))
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    code = await WaitForCallbackAsync(listener, state, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                listener.Stop();
            }

            return await RequestTokenAsync(http, new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = clientId,
                ["code_verifier"] = verifier,
            }).ConfigureAwait(false);
        }

        public static Task<TokenResponse> RefreshAsync(HttpClient http, string clientId, string refreshToken)
        {
            return RequestTokenAsync(http, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = clientId,
            });
        }

        private static async Task<string> WaitForCallbackAsync(TcpListener listener, string expectedState, CancellationToken cancellationToken)
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                using (client)
                using (var stream = client.GetStream())
                {
                    var requestLine = await ReadRequestLineAsync(stream).ConfigureAwait(false);
                    // e.g. "GET /callback?code=...&state=... HTTP/1.1"
                    var parts = requestLine?.Split(' ');
                    if (parts == null || parts.Length < 2 || !parts[1].StartsWith("/callback", StringComparison.Ordinal))
                    {
                        // Browsers also ask for /favicon.ico etc.
                        await WriteResponseAsync(stream, 404, "Not found").ConfigureAwait(false);
                        continue;
                    }

                    var query = ParseQuery(parts[1]);
                    query.TryGetValue("state", out var state);
                    query.TryGetValue("code", out var code);
                    query.TryGetValue("error", out var error);

                    if (state != expectedState)
                    {
                        await WriteResponseAsync(stream, 400, Page("Login failed", "The response didn't match this login attempt. Please try again from MusicBee.")).ConfigureAwait(false);
                        throw new SpotifyAuthException("Spotify login response didn't match the request (state mismatch).");
                    }
                    if (!string.IsNullOrEmpty(error))
                    {
                        await WriteResponseAsync(stream, 200, Page("Login cancelled", "SpotiBee wasn't given access. You can close this tab.")).ConfigureAwait(false);
                        throw new SpotifyAuthException(error == "access_denied" ? "Spotify access was denied." : $"Spotify login failed: {error}");
                    }
                    if (string.IsNullOrEmpty(code))
                    {
                        await WriteResponseAsync(stream, 400, Page("Login failed", "No authorisation code was returned.")).ConfigureAwait(false);
                        throw new SpotifyAuthException("Spotify didn't return an authorisation code.");
                    }

                    await WriteResponseAsync(stream, 200, Page("Connected to Spotify", "SpotiBee is connected. You can close this tab and return to MusicBee.")).ConfigureAwait(false);
                    return code;
                }
            }
        }

        private static async Task<TokenResponse> RequestTokenAsync(HttpClient http, Dictionary<string, string> form)
        {
            using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form)).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Json.TryParse<TokenError>(body, out var error);
                throw new SpotifyAuthException(
                    error?.Description ?? error?.Error ?? $"Token request failed ({(int)response.StatusCode})",
                    isInvalidGrant: error?.Error == "invalid_grant");
            }
            return Json.Parse<TokenResponse>(body);
        }

        private static async Task<string> ReadRequestLineAsync(Stream stream)
        {
            // Only the first line matters; read until CRLF or a sane limit
            var buffer = new byte[4096];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer, total, buffer.Length - total).ConfigureAwait(false);
                if (read == 0)
                    break;
                total += read;
                var text = Encoding.ASCII.GetString(buffer, 0, total);
                var end = text.IndexOf("\r\n", StringComparison.Ordinal);
                if (end >= 0)
                    return text.Substring(0, end);
            }
            return total > 0 ? Encoding.ASCII.GetString(buffer, 0, total) : null;
        }

        private static async Task WriteResponseAsync(Stream stream, int status, string html)
        {
            var body = Encoding.UTF8.GetBytes(html);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
        }

        private static string Page(string title, string message) =>
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>SpotiBee</title>" +
            "<style>body{font-family:Segoe UI,sans-serif;background:#121212;color:#eee;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}" +
            "div{text-align:center}h1{color:#f5b301;font-weight:600}</style></head>" +
            $"<body><div><h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(message)}</p></div></body></html>";

        private static Dictionary<string, string> ParseQuery(string pathAndQuery)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var q = pathAndQuery.IndexOf('?');
            if (q < 0)
                return result;
            foreach (var pair in pathAndQuery.Substring(q + 1).Split('&'))
            {
                var eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString(eq < 0 ? pair : pair.Substring(0, eq));
                var value = eq < 0 ? "" : Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                result[key] = value;
            }
            return result;
        }

        private static string FormEncode(Dictionary<string, string> values) =>
            string.Join("&", values.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

        private static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return bytes;
        }

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public class SpotifyAuthException : Exception
    {
        public SpotifyAuthException(string message, bool isInvalidGrant = false) : base(message)
        {
            IsInvalidGrant = isInvalidGrant;
        }

        /// <summary>The refresh token was revoked or expired; the user must log in again.</summary>
        public bool IsInvalidGrant { get; }
    }
}
