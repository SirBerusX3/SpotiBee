using System.Linq;
using System.Runtime.Serialization;

namespace SpotiBee.Spotify
{
    // Only the fields SpotiBee uses are mapped; DataContractJsonSerializer ignores the rest.

    [DataContract]
    public class TokenResponse
    {
        [DataMember(Name = "access_token")] public string AccessToken { get; set; }
        [DataMember(Name = "token_type")] public string TokenType { get; set; }
        [DataMember(Name = "scope")] public string Scope { get; set; }
        [DataMember(Name = "expires_in")] public int ExpiresIn { get; set; }
        [DataMember(Name = "refresh_token")] public string RefreshToken { get; set; }
    }

    [DataContract]
    public class TokenError
    {
        [DataMember(Name = "error")] public string Error { get; set; }
        [DataMember(Name = "error_description")] public string Description { get; set; }
    }

    [DataContract]
    public class ApiErrorEnvelope
    {
        [DataMember(Name = "error")] public ApiError Error { get; set; }
    }

    [DataContract]
    public class ApiError
    {
        [DataMember(Name = "status")] public int Status { get; set; }
        [DataMember(Name = "message")] public string Message { get; set; }
        [DataMember(Name = "reason")] public string Reason { get; set; }
    }

    [DataContract]
    public class UserProfile
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "display_name")] public string DisplayName { get; set; }
        // Spotify stopped returning this to Development Mode apps in Feb 2026; null means unknown
        [DataMember(Name = "product")] public string Product { get; set; }

        public bool? IsPremium => Product == null ? (bool?)null : Product == "premium";
    }

    [DataContract]
    public class PlaybackState
    {
        [DataMember(Name = "device")] public Device Device { get; set; }
        [DataMember(Name = "is_playing")] public bool IsPlaying { get; set; }
        [DataMember(Name = "progress_ms")] public int? ProgressMs { get; set; }
        [DataMember(Name = "shuffle_state")] public bool ShuffleState { get; set; }
        [DataMember(Name = "repeat_state")] public string RepeatState { get; set; }
        [DataMember(Name = "currently_playing_type")] public string CurrentlyPlayingType { get; set; }
        [DataMember(Name = "item")] public Track Item { get; set; }
    }

    [DataContract]
    public class Device
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "type")] public string Type { get; set; }
        [DataMember(Name = "is_active")] public bool IsActive { get; set; }
        [DataMember(Name = "is_restricted")] public bool IsRestricted { get; set; }
        [DataMember(Name = "volume_percent")] public int? VolumePercent { get; set; }

        public override string ToString() => $"{Name} ({Type})";
    }

    [DataContract]
    public class DeviceList
    {
        [DataMember(Name = "devices")] public Device[] Devices { get; set; }
    }

    // Also used for podcast episodes (currently_playing_type = "episode"), where
    // artists/album are absent and Show is populated instead.
    [DataContract]
    public class Track
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "uri")] public string Uri { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "duration_ms")] public int DurationMs { get; set; }
        [DataMember(Name = "is_local")] public bool IsLocal { get; set; }
        [DataMember(Name = "artists")] public Artist[] Artists { get; set; }
        [DataMember(Name = "album")] public Album Album { get; set; }
        [DataMember(Name = "show")] public Show Show { get; set; }
        [DataMember(Name = "images")] public Image[] Images { get; set; }

        public string ArtistNames =>
            Artists != null && Artists.Length > 0
                ? string.Join(", ", Artists.Select(a => a.Name))
                : Show?.Name ?? "";

        public string AlbumName => Album?.Name ?? "";

        public Image[] Artwork => Album?.Images ?? Images ?? Show?.Images;
    }

    [DataContract]
    public class Artist
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "uri")] public string Uri { get; set; }
    }

    [DataContract]
    public class Album
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "uri")] public string Uri { get; set; }
        [DataMember(Name = "images")] public Image[] Images { get; set; }
    }

    [DataContract]
    public class Show
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "images")] public Image[] Images { get; set; }
    }

    [DataContract]
    public class Image
    {
        [DataMember(Name = "url")] public string Url { get; set; }
        [DataMember(Name = "width")] public int? Width { get; set; }
        [DataMember(Name = "height")] public int? Height { get; set; }
    }

    // Request bodies

    [DataContract]
    public class PlayRequest
    {
        [DataMember(Name = "uris", EmitDefaultValue = false)] public string[] Uris { get; set; }
        [DataMember(Name = "context_uri", EmitDefaultValue = false)] public string ContextUri { get; set; }
        [DataMember(Name = "position_ms", EmitDefaultValue = false)] public int? PositionMs { get; set; }
    }

    [DataContract]
    public class TransferRequest
    {
        [DataMember(Name = "device_ids")] public string[] DeviceIds { get; set; }
        [DataMember(Name = "play")] public bool Play { get; set; }
    }
}
