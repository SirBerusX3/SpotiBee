using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using SpotiBee;
using SpotiBee.Library;
using SpotiBee.Playback;
using SpotiBee.UI;

namespace MusicBeePlugin
{
    // MusicBee finds the plugin by the class name MusicBeePlugin.Plugin
    public partial class Plugin
    {
        private const string PluginName = "SpotiBee";

        private MusicBeeApiInterface mbApiInterface;
        private readonly PluginInfo about = new PluginInfo();
        private SpotiBeeController controller;
        private PlaybackRouter router;
        private IMusicBeePlayer player;
        private System.Windows.Forms.Timer syncTimer;
        private Control uiThread;
        private SynchronizationContext uiContext;
        private SkinColours skin;
        private string storageDir;
        private bool menusAdded;

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            mbApiInterface = new MusicBeeApiInterface();
            mbApiInterface.Initialise(apiInterfacePtr);

            about.PluginInfoVersion = PluginInfoVersion;
            about.Name = PluginName;
            about.Description = "Bridge between MusicBee and Spotify: control playback and manage playlists";
            about.Author = "SpotiBee";
            about.TargetApplication = PluginName;   // header text of the dockable panel
            about.Type = PluginType.PanelView;
            about.VersionMajor = 0;
            about.VersionMinor = 3;
            about.Revision = 0;
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents;
            about.ConfigurationPanelHeight = 0;     // we show our own settings window

            storageDir = Path.Combine(mbApiInterface.Setting_GetPersistentStoragePath(), PluginName);
            controller = new SpotiBeeController(Path.Combine(storageDir, "settings.json"));
            controller.StatusMessage += message => mbApiInterface.MB_Trace?.Invoke("SpotiBee: " + message);

            player = new MusicBeePlayer(mbApiInterface);
            router = new PlaybackRouter(player, controller.RouterPlayback, controller.Store,
                path => PlaceholderWriter.IsUnder(path, controller.Settings.EffectivePlaceholderFolder),
                controller.Settings);
            router.StatusMessage += controller.ShowStatus;
            controller.Router = router;
            return about;
        }

        public bool Configure(IntPtr panelHandle)
        {
            ShowSettings();
            return true;
        }

        // Settings are saved as they change, so there's nothing to do on Apply/Save
        public void SaveSettings()
        {
        }

        public void Close(PluginCloseReason reason)
        {
            syncTimer?.Dispose();
            syncTimer = null;
            router?.Shutdown();
            controller?.Dispose();
            controller = null;
        }

        public void Uninstall()
        {
            Close(PluginCloseReason.UserDisabled);
            try
            {
                if (Directory.Exists(storageDir))
                    Directory.Delete(storageDir, recursive: true);
            }
            catch (IOException)
            {
                // Nothing important is lost if cleanup fails
            }
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            switch (type)
            {
                case NotificationType.PluginStartup:
                    Startup();
                    break;
                case NotificationType.TrackChanged:
                    OnUi(() => router.OnTrackChanged(sourceFileUrl ?? player.NowPlayingFile));
                    break;
                case NotificationType.PlayStateChanged:
                    OnUi(router.OnPlayStateChanged);
                    break;
                case NotificationType.VolumeLevelChanged:
                    OnUi(router.OnVolumeChanged);
                    break;
                case NotificationType.VolumeMuteChanged:
                    OnUi(router.OnMuteChanged);
                    break;
                case NotificationType.PlayerScrobbleChanged:
                    OnUi(router.OnScrobbleChanged);
                    break;
            }
        }

        private void Startup()
        {
            uiThread = Control.FromHandle(mbApiInterface.MB_GetWindowHandle());
            uiContext = SynchronizationContext.Current;
            AddMenuItems();

            controller.PlaybackUpdated += snapshot => OnUi(() => router.OnSnapshot(snapshot));
            controller.PollFailed += _ => OnUi(router.OnSpotifyPollFailed);
            controller.Start();

            OnUi(() =>
            {
                router.RecoverFromPreviousSession();
                syncTimer = new System.Windows.Forms.Timer { Interval = 500 };
                syncTimer.Tick += (s, e) => router.Tick();
                syncTimer.Start();

                // MusicBee may already be playing (e.g. resumed on startup)
                var state = player.PlayState;
                if (state == PlayState.Playing || state == PlayState.Paused)
                    router.OnTrackChanged(player.NowPlayingFile);
            });
        }

        /// <summary>
        /// Runs on MusicBee's UI thread, always deferred, so the router never re-enters itself
        /// through MusicBee notifications raised by its own player calls.
        /// </summary>
        private void OnUi(Action action)
        {
            void Safe()
            {
                try
                {
                    if (router != null && controller != null)
                        action();
                }
                catch (Exception ex)
                {
                    mbApiInterface.MB_Trace?.Invoke("SpotiBee router error: " + ex);
                }
            }

            if (uiThread != null && !uiThread.IsDisposed && uiThread.IsHandleCreated)
                uiThread.BeginInvoke((Action)Safe);
            else if (uiContext != null)
                uiContext.Post(_ => Safe(), null);
            else
                Safe();
        }

        //  presence of this function tells MusicBee the plugin has a dockable panel
        public int OnDockablePanelCreated(Control panel)
        {
            skin ??= SkinColours.FromMusicBee(mbApiInterface);
            var view = new NowPlayingPanel(controller, skin, ShowSettings) { Dock = DockStyle.Fill };
            panel.Controls.Add(view);
            return view.PreferredHeight;
        }

        //  menu shown when the panel header is clicked
        public List<ToolStripItem> GetHeaderMenuItems()
        {
            var mode = new ToolStripMenuItem("Playback mode");
            foreach (PlaybackMode m in Enum.GetValues(typeof(PlaybackMode)))
            {
                var value = m;
                mode.DropDownItems.Add(new ToolStripMenuItem(PlaybackRouter.Describe(m), null, (s, e) => router.Mode = value)
                {
                    Checked = router.Mode == m,
                });
            }

            return new List<ToolStripItem>
            {
                mode,
                new ToolStripSeparator(),
                new ToolStripMenuItem(controller.IsConnected ? "Settings / Disconnect…" : "Connect to Spotify…", null, (s, e) => ShowSettings()),
                new ToolStripMenuItem("Import Spotify playlists…", null, (s, e) => ShowImport()) { Enabled = controller.IsConnected },
                new ToolStripMenuItem("Open Spotify", null, (s, e) => controller.OpenSpotifyApp()),
            };
        }

        private void AddMenuItems()
        {
            if (menusAdded)
                return;
            menusAdded = true;

            mbApiInterface.MB_AddMenuItem("mnuTools/SpotiBee Settings…", null, (s, e) => ShowSettings());
            mbApiInterface.MB_AddMenuItem("mnuTools/SpotiBee: Import Spotify Playlists…", null, (s, e) => ShowImport());

            // A hotkey description makes the command assignable in Preferences > Hotkeys
            mbApiInterface.MB_AddMenuItem("mnuTools/SpotiBee: Play or Pause", "SpotiBee: Play/Pause", async (s, e) => await controller.PlayPauseAsync());
            mbApiInterface.MB_AddMenuItem("mnuTools/SpotiBee: Next Track", "SpotiBee: Next Track", async (s, e) => await controller.NextAsync());
            mbApiInterface.MB_AddMenuItem("mnuTools/SpotiBee: Previous Track", "SpotiBee: Previous Track", async (s, e) => await controller.PreviousAsync());
            mbApiInterface.MB_AddMenuItem("mnuTools/SpotiBee: Switch Playback Mode", "SpotiBee: Switch Playback Mode", (s, e) => router.CycleMode());
        }

        private void ShowSettings()
        {
            using var form = new SettingsForm(controller);
            form.ShowDialog(MainWindow);
        }

        private void ShowImport()
        {
            if (!controller.IsConnected)
            {
                ShowSettings();
                return;
            }
            using var form = new ImportForm(controller, new MusicBeeLibrary(mbApiInterface));
            form.ShowDialog(MainWindow);
        }

        private IWin32Window MainWindow => Control.FromHandle(mbApiInterface.MB_GetWindowHandle());
    }
}
