using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SpotiBee.Spotify;

namespace SpotiBee
{
    /// <summary>
    /// A short report to paste into a forum post. It's posted publicly, so it holds nothing private:
    /// no login details, account name or Client ID. Device and computer names become their type,
    /// and the Windows user folder in paths is masked.
    /// </summary>
    public static class DiagnosticsSummary
    {
        public sealed class Inputs
        {
            public string Version;
            public int MusicBeeApiRevision;
            public string Windows;
            public bool Is64Bit;
            public bool HasClientId;
            public bool Connected;
            public bool? Premium;
            public string PlaybackMode;
            public string Route;
            public IReadOnlyList<Device> Devices;
            public string DevicesError;
            public ICollection<string> AlbumPlayDeviceIds;
            public int LinkedPlaylists;
            public int PausedPlaylists;
            public int ImportedAlbums;
            public int Placeholders;
            public bool AutoSync;
            public bool AutoImport;
            public bool UpdateCheck;
            public bool Lyrics;
            public IEnumerable<string> Log;

            // Things to keep out of the text
            public string UserProfileFolder;
            public string MachineName;
            public IEnumerable<string> AccountNames;
        }

        private const int MaxLogLines = 25;
        private const int MaxLineLength = 300;

        // Lines that say something went wrong; ordinary chatter is left out
        private static readonly string[] ProblemWords =
        {
            "fail", "couldn't", "error", "refused", "restriction", "no active", "play check", "exception", "not found",
            "Spotify GET ", "Spotify PUT ", "Spotify POST ", "Spotify DELETE ",
        };

        public static string Build(Inputs i)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[code]");
            sb.AppendLine($"SpotiBee {i.Version}, MusicBee API {i.MusicBeeApiRevision}, {i.Windows}, {(i.Is64Bit ? "64" : "32")}-bit MusicBee");
            sb.AppendLine("Spotify: " + (!i.HasClientId ? "not set up (no Client ID)"
                : !i.Connected ? "Client ID set, not connected"
                : "connected" + (i.Premium == false ? ", account isn't Premium" : "")));
            sb.AppendLine($"Playback: mode={i.PlaybackMode}, now={i.Route}");

            if (i.DevicesError != null)
                sb.AppendLine("Devices: couldn't list (" + i.DevicesError + ")");
            else if (i.Devices == null || i.Devices.Count == 0)
                sb.AppendLine("Devices: none found");
            else
            {
                sb.AppendLine("Devices:");
                foreach (var d in i.Devices)
                {
                    var notes = new List<string>();
                    if (d.IsActive) notes.Add("active");
                    if (d.IsRestricted) notes.Add("can't be controlled");
                    if (d.Id != null && i.AlbumPlayDeviceIds?.Contains(d.Id) == true) notes.Add("plays tracks via their album");
                    sb.AppendLine($"  {TypeName(d)}" + (notes.Count > 0 ? " (" + string.Join(", ", notes) + ")" : ""));
                }
            }

            sb.AppendLine($"Library: {i.LinkedPlaylists} linked playlist(s)" + (i.PausedPlaylists > 0 ? $" ({i.PausedPlaylists} paused)" : "") +
                          $", {i.ImportedAlbums} imported album(s), {i.Placeholders} placeholder(s)");
            sb.AppendLine($"Settings: auto sync {OnOff(i.AutoSync)}, auto import {OnOff(i.AutoImport)}, " +
                          $"update check {OnOff(i.UpdateCheck)}, lyrics {OnOff(i.Lyrics)}");

            var problems = (i.Log ?? Enumerable.Empty<string>())
                .Select(l => (l ?? "").Split('\n')[0].TrimEnd('\r'))   // an exception's first line, not its stack
                .Where(IsProblem)
                .ToList();
            var shown = problems.Skip(Math.Max(0, problems.Count - MaxLogLines)).ToList();
            sb.AppendLine(shown.Count == 0 ? "Recent problems: none logged" : $"Recent problems (last {shown.Count}):");
            foreach (var line in shown)
                sb.AppendLine("  " + Truncate(Mask(line, i)));
            sb.Append("[/code]");
            return Mask(sb.ToString(), i);
        }

        private static bool IsProblem(string line) =>
            ProblemWords.Any(w => line.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>Hides the Windows user folder, the computer's name, device names and the account's name.</summary>
        public static string Mask(string text, Inputs i)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? "";
            text = Replace(text, i.UserProfileFolder, "%USERPROFILE%");
            foreach (var d in i.Devices ?? new Device[0])
                if (!string.IsNullOrWhiteSpace(d.Name) && d.Name.Length > 2)
                    text = Replace(text, d.Name, "[" + TypeName(d) + "]");
            text = Replace(text, i.MachineName, "[this PC]");
            foreach (var name in i.AccountNames ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(name) && name.Length > 2)
                    text = Replace(text, name, "[you]");
            return text;
        }

        private static string TypeName(Device d) => string.IsNullOrEmpty(d.Type) ? "device" : d.Type;

        private static string Replace(string text, string find, string with)
        {
            if (string.IsNullOrEmpty(find))
                return text;
            var sb = new StringBuilder();
            var at = 0;
            int hit;
            while ((hit = text.IndexOf(find, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                sb.Append(text, at, hit - at).Append(with);
                at = hit + find.Length;
            }
            return sb.Append(text, at, text.Length - at).ToString();
        }

        private static string Truncate(string line) => line.Length <= MaxLineLength ? line : line.Substring(0, MaxLineLength) + "…";

        private static string OnOff(bool on) => on ? "on" : "off";
    }
}
