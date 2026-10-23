using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace SpotiBee.UI
{
    /// <summary>Shareable open.spotify.com links for Spotify URIs, and copying them.</summary>
    public static class SpotifyLinks
    {
        /// <summary>"spotify:track:abc" → "https://open.spotify.com/track/abc"; null if it isn't a Spotify URI.</summary>
        public static string Web(string uri)
        {
            var parts = (uri ?? "").Split(':');
            // The type and ID are always the last two parts (old playlist URIs have a user in between)
            if (parts.Length < 3 || parts[0] != "spotify" || parts.Skip(1).Any(string.IsNullOrEmpty))
                return null;
            return $"https://open.spotify.com/{parts[parts.Length - 2]}/{Uri.EscapeDataString(parts[parts.Length - 1])}";
        }

        /// <summary>
        /// Puts the links for these URIs on the clipboard, one per line. Returns how many were copied;
        /// 0 if there was nothing to copy or another program had the clipboard locked.
        /// </summary>
        public static int Copy(IEnumerable<string> uris)
        {
            var links = uris.Select(Web).Where(l => l != null).Distinct().ToList();
            if (links.Count == 0)
                return 0;
            try
            {
                // Other programs (clipboard managers, remote desktop) can hold the clipboard briefly
                Clipboard.SetDataObject(string.Join(Environment.NewLine, links), copy: true, retryTimes: 5, retryDelay: 100);
                return links.Count;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException || ex is System.Threading.ThreadStateException)
            {
                Diagnostics.Log("Copying Spotify link", ex);
                return 0;
            }
        }

        /// <summary>"Copied the Spotify link." / "Copied 3 Spotify links." / an apology.</summary>
        public static string Describe(int copied, string what = "Spotify link") =>
            copied == 0 ? "Couldn't copy the link. Another program may be using the clipboard; try again."
            : copied == 1 ? $"Copied the {what}."
            : $"Copied {copied} {what}s.";
    }
}
