using static MusicBeePlugin.Plugin;

namespace SpotiBee.Playback
{
    public sealed class MusicBeePlayer : IMusicBeePlayer
    {
        private readonly MusicBeeApiInterface api;

        public MusicBeePlayer(MusicBeeApiInterface api)
        {
            this.api = api;
        }

        public string NowPlayingFile => api.NowPlaying_GetFileUrl();
        public PlayState PlayState => api.Player_GetPlayState();

        public int PositionMs
        {
            get => api.Player_GetPosition();
            set => api.Player_SetPosition(value);
        }

        public int DurationMs => api.NowPlaying_GetDuration();

        public float Volume
        {
            get => api.Player_GetVolume();
            set => api.Player_SetVolume(value);
        }

        public bool Muted
        {
            get => api.Player_GetMute();
            set => api.Player_SetMute(value);
        }

        public void PlayPause() => api.Player_PlayPause();
        public void Next() => api.Player_PlayNextTrack();
        public void Previous() => api.Player_PlayPreviousTrack();
    }
}
