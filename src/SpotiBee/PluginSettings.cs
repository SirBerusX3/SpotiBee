using System;
using System.IO;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace SpotiBee
{
    [DataContract]
    public class PluginSettings
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SpotiBee.RefreshToken");

        [DataMember] public string ClientId { get; set; } = "";

        // DPAPI-encrypted for the current Windows user, base64 encoded
        [DataMember] public string ProtectedRefreshToken { get; set; }

        [DataMember] public string PreferredDeviceId { get; set; }

        /// <summary>Where placeholder files go; null means the default under the user's Music folder.</summary>
        [DataMember] public string PlaceholderFolder { get; set; }

        public static string DefaultPlaceholderFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "SpotiBee");

        [IgnoreDataMember]
        public string EffectivePlaceholderFolder =>
            string.IsNullOrWhiteSpace(PlaceholderFolder) ? DefaultPlaceholderFolder : PlaceholderFolder;

        /// <summary>A Playback.PlaybackMode value; 0 (Local first) by default.</summary>
        [DataMember] public int PlaybackMode { get; set; }

        /// <summary>Off by default, so MusicBee doesn't scrobble tracks Spotify is already scrobbling.</summary>
        [DataMember] public bool AllowMusicBeeScrobblesForSpotify { get; set; }

        // Changes SpotiBee made to MusicBee's player, persisted so they can be undone after a crash
        [DataMember] public bool ScrobbleSuppressedBySpotiBee { get; set; }
        [DataMember] public bool MuteAppliedBySpotiBee { get; set; }

        [IgnoreDataMember] public string FilePath { get; private set; }

        [IgnoreDataMember]
        public string RefreshToken
        {
            get
            {
                if (string.IsNullOrEmpty(ProtectedRefreshToken))
                    return null;
                try
                {
                    var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedRefreshToken), Entropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(bytes);
                }
                catch (CryptographicException)
                {
                    // Settings copied from another user or machine
                    return null;
                }
            }
            set
            {
                ProtectedRefreshToken = string.IsNullOrEmpty(value)
                    ? null
                    : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
            }
        }

        public static PluginSettings Load(string filePath)
        {
            PluginSettings settings = null;
            if (File.Exists(filePath))
                Json.TryParse(File.ReadAllText(filePath), out settings);
            settings ??= new PluginSettings();
            settings.FilePath = filePath;
            return settings;
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, Json.Stringify(this));
            if (File.Exists(FilePath))
                File.Replace(temp, FilePath, null);
            else
                File.Move(temp, FilePath);
        }
    }
}
