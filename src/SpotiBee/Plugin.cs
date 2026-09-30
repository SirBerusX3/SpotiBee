using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            var trace = mbApiInterface.MB_Trace;
            Diagnostics.Sink = trace == null ? (Action<string>)null : message => trace(message);
            Diagnostics.Log($"Initialise (v{about.VersionMajor}.{about.VersionMinor}, MusicBee API {mbApiInterface.ApiRevision})");

            storageDir = Path.Combine(mbApiInterface.Setting_GetPersistentStoragePath(), PluginName);
            controller = new SpotiBeeController(Path.Combine(storageDir, "settings.json"));
            controller.StatusMessage += Diagnostics.Log;

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
            Diagnostics.Log("Close: " + reason);
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
            }
        }

        private void Startup()
        {
            Diagnostics.Log("Startup");
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
                    Diagnostics.Log("Router", ex);
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
            Diagnostics.Log($"OnDockablePanelCreated: host={panel?.GetType().FullName} size={panel?.Size} controller={(controller != null)}");
            try
            {
                skin ??= SkinColours.FromMusicBee(mbApiInterface);
                var view = new NowPlayingPanel(controller, skin, ShowSettings) { Dock = DockStyle.Fill };
                panel.Controls.Add(view);
                return view.PreferredHeight;
            }
            catch (Exception ex)
            {
                // Show the problem instead of leaving an empty panel
                Diagnostics.Log("Creating the panel", ex);
                panel.Controls.Add(new Label
                {
                    Dock = DockStyle.Fill,
                    Text = "SpotiBee panel failed to load:\n" + ex.Message + "\n\nTools > SpotiBee: Save Diagnostics Report",
                    TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                });
                return 120;
            }
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
            mbApiInterface.MB_AddMenuItem("mnuTools/SpotiBee: Save Diagnostics Report", null, (s, e) => SaveDiagnostics());
        }

        private void SaveDiagnostics()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"SpotiBee {about.VersionMajor}.{about.VersionMinor} diagnostics, {DateTime.Now}");
            sb.AppendLine($"MusicBee API revision {mbApiInterface.ApiRevision}, {Environment.OSVersion}, {(Environment.Is64BitProcess ? "64" : "32")}-bit");
            sb.AppendLine($"Connected={controller?.IsConnected} User={controller?.User?.DisplayName ?? "(unknown)"}");
            sb.AppendLine($"Mode={router?.Mode} Route={router?.Route}");
            sb.AppendLine($"Player: state={player?.PlayState} file={player?.NowPlayingFile}");
            var s = controller?.LastSnapshot?.State;
            sb.AppendLine($"Spotify: playing={s?.IsPlaying} item={s?.Item?.Name ?? "(none)"} device={s?.Device?.Name ?? "(none)"}");
            sb.AppendLine($"Placeholder folder={controller?.Settings.EffectivePlaceholderFolder}");
            sb.AppendLine();
            var panels = NowPlayingPanel.LivePanels.ToList();
            sb.AppendLine($"Live panels: {panels.Count}");
            foreach (var p in panels)
                sb.AppendLine(p.Describe());
            sb.AppendLine("Recent log:");
            foreach (var line in Diagnostics.History)
                sb.AppendLine("  " + line);

            var path = Path.Combine(storageDir, "diagnostics.txt");
            try
            {
                Directory.CreateDirectory(storageDir);
                File.WriteAllText(path, sb.ToString());
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't save the diagnostics report: " + ex.Message, "SpotiBee");
            }
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
