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
        private SyncScheduler playlistSync;
        private MusicBeeLibrary library;
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
            // Version comes from <Version> in SpotiBee.csproj
            var version = typeof(Plugin).Assembly.GetName().Version;
            about.VersionMajor = (short)version.Major;
            about.VersionMinor = (short)version.Minor;
            about.Revision = (short)version.Build;
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents;
            about.ConfigurationPanelHeight = 0;     // we show our own settings window

            var trace = mbApiInterface.MB_Trace;
            Diagnostics.Sink = trace == null ? (Action<string>)null : message => trace(message);
            Diagnostics.Log($"Initialise (v{version.ToString(3)}, MusicBee API {mbApiInterface.ApiRevision})");

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
            tidyTimer?.Dispose();
            tidyTimer = null;
            playlistSync?.Dispose();
            playlistSync = null;
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
                case NotificationType.PlaylistUpdated:
                case NotificationType.PlaylistDeleted:
                case NotificationType.PlaylistMoved:
                    OnUi(() => playlistSync?.OnMusicBeePlaylistChanged(sourceFileUrl));
                    break;
                case NotificationType.VolumeMuteChanged:
                    OnUi(router.OnMuteChanged);
                    break;
                case NotificationType.NowPlayingLyricsReady:
                    // MusicBee found lyrics for a placeholder it is playing: keep them in the file
                    OnUi(() =>
                    {
                        var file = player.NowPlayingFile;
                        if (controller.Lyrics != null && PlaceholderWriter.IsUnder(file, controller.Settings.EffectivePlaceholderFolder))
                            controller.Lyrics.SaveDownloaded(file, mbApiInterface.NowPlaying_GetDownloadedLyrics());
                    });
                    break;
                case NotificationType.FileAddedToLibrary:
                case NotificationType.FileAddedToInbox:
                    // A folder scan sends one notification per file: check once things settle
                    OnUi(() => upgradeDue = DateTime.UtcNow.AddSeconds(5));
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

                // Timers must be created on the UI thread, so the playlist scheduler starts here too
                library = new MusicBeeLibrary(mbApiInterface);
                controller.Lyrics = new LyricsService(controller.Http, library, controller.Store);
                NowPlayingPanel.RefreshLyricsSetting();
                playlistSync = new SyncScheduler(controller, library);
                playlistSync.Start();

                tidyTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                tidyTimer.Tick += (s, e) => UpgradeAddedFiles();
                tidyTimer.Start();

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
            skin ??= SkinColours.FromMusicBee(mbApiInterface);

            void Create()
            {
                try
                {
                    var view = new NowPlayingPanel(controller, skin, ShowSettings, ShowSearch) { Dock = DockStyle.Fill };
                    panel.Controls.Add(view);
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
                }
            }

            // When MusicBee restores a saved layout at startup it calls this from a background
            // thread, but the host panel belongs to the UI thread and WinForms controls can only be
            // parented on the thread that owns them. Build the panel over there instead.
            var main = Control.FromHandle(mbApiInterface.MB_GetWindowHandle());
            if (main != null && main.IsHandleCreated && main.InvokeRequired)
            {
                Diagnostics.Log("Panel requested from a background thread; creating it on the UI thread");
                main.BeginInvoke((Action)Create);
            }
            else if (panel.IsHandleCreated && panel.InvokeRequired)
            {
                Diagnostics.Log("Panel requested from a background thread; creating it via the host panel");
                panel.BeginInvoke((Action)Create);
            }
            else
            {
                Create();
            }
            // Negative: the panel is resizable and fills its slot, so lyrics can use any extra height
            return -1;
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
                new ToolStripMenuItem("Show lyrics", null, (s, e) =>
                {
                    controller.Settings.HideLyrics = !controller.Settings.HideLyrics;
                    try { controller.Settings.Save(); } catch (Exception ex) { Diagnostics.Log("Saving settings", ex); }
                    NowPlayingPanel.RefreshLyricsSetting();
                }) { Checked = !controller.Settings.HideLyrics, ToolTipText = "Lyrics for whatever Spotify is playing outside MusicBee" },
                new ToolStripSeparator(),
                new ToolStripMenuItem(controller.IsConnected ? "Settings / Disconnect…" : "Connect to Spotify…", null, (s, e) => ShowSettings()),
                new ToolStripMenuItem("Search Spotify…", null, (s, e) => ShowSearch()) { Enabled = controller.IsConnected },
                new ToolStripMenuItem("Import Spotify playlists…", null, (s, e) => ShowImport()) { Enabled = controller.IsConnected },
                new ToolStripMenuItem("Send playlists to Spotify…", null, (s, e) => ShowSendToSpotify()) { Enabled = controller.IsConnected },
                new ToolStripMenuItem("Sync playlists now", null, (s, e) => SyncNow()) { Enabled = controller.IsConnected },
                new ToolStripMenuItem("Open Spotify", null, (s, e) => controller.OpenSpotifyApp()),
            };
        }

        private void AddMenuItems()
        {
            if (menusAdded)
                return;
            menusAdded = true;

            mbApiInterface.MB_AddMenuItem("mnuTools/SpotiBee Settings…", null, (s, e) => ShowSettings());
            mbApiInterface.MB_AddMenuItem("mnuTools/SpotiBee: Search Spotify…", "SpotiBee: Search Spotify", (s, e) => ShowSearch());
            mbApiInterface.MB_AddMenuItem("mnuTools/SpotiBee: Import Spotify Playlists…", null, (s, e) => ShowImport());
            mbApiInterface.MB_AddMenuItem("mnuTools/SpotiBee: Send Playlists to Spotify…", null, (s, e) => ShowSendToSpotify());
            mbApiInterface.MB_AddMenuItem("mnuTools/SpotiBee: Sync Playlists Now", "SpotiBee: Sync Playlists Now", (s, e) => SyncNow());
            mbApiInterface.MB_AddMenuItem("mnuTools/SpotiBee: Tracks Not Found on Spotify…", null, (s, e) => ShowUnmatched());
            mbApiInterface.MB_AddMenuItem("mnuTools/SpotiBee: Clean Up Unused Placeholders…", null, (s, e) => CleanUpPlaceholders());

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
            sb.AppendLine($"SpotiBee {typeof(Plugin).Assembly.GetName().Version.ToString(3)} diagnostics, {DateTime.Now}");
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
            using var form = new ImportForm(controller, library ?? new MusicBeeLibrary(mbApiInterface), playlistSync);
            form.ShowDialog(MainWindow);
        }

        private void ShowSendToSpotify()
        {
            if (!controller.IsConnected || playlistSync == null)
            {
                ShowSettings();
                return;
            }
            using var form = new SendToSpotifyForm(controller, library, playlistSync);
            form.ShowDialog(MainWindow);
        }

        private SearchForm searchWindow;
        private System.Windows.Forms.Timer tidyTimer;
        /// <summary>When to next look for local copies of placeholders; null when nothing is pending.</summary>
        private DateTime? upgradeDue = DateTime.UtcNow.AddSeconds(30);

        private LibraryTidy Tidy()
        {
            var folder = controller.Settings.EffectivePlaceholderFolder;
            return new LibraryTidy(library, controller.Store, p => PlaceholderWriter.IsUnder(p, folder), folder);
        }

        /// <summary>
        /// Swaps local copies in for placeholders across the whole library: shortly after startup,
        /// after files are added, and before a manual sync. Not tied to the "file added"
        /// notification alone, since MusicBee doesn't send it for every way of adding files.
        /// </summary>
        private void UpgradeAddedFiles(bool now = false)
        {
            if (library == null || controller == null || (!now && (upgradeDue == null || DateTime.UtcNow < upgradeDue)))
                return;
            upgradeDue = null;
            try
            {
                var upgraded = Tidy().UpgradePlaceholdersFromLibrary();
                if (upgraded > 0)
                    controller.ShowStatus($"Your own copy of {upgraded} song(s) now replaces the Spotify placeholder in your playlists.");
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Upgrading placeholders", ex);
            }
        }

        private void CleanUpPlaceholders()
        {
            if (library == null)
                return;
            var tidy = Tidy();
            var orphans = tidy.FindOrphans(player.NowPlayingFile);
            if (orphans.Count == 0)
            {
                MessageBox.Show(MainWindow, "Every Spotify placeholder is still in one of your playlists. Nothing to clean up.",
                    "SpotiBee", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var megabytes = orphans.Sum(o => o.Bytes) / 1024.0 / 1024.0;
            var answer = MessageBox.Show(MainWindow,
                $"{orphans.Count} Spotify placeholder(s) ({megabytes:0.#} MB) aren't in any of your MusicBee playlists any more " +
                "(auto-playlists like Recently Added don't count, since they just show what's in the library).\n\n" +
                "Delete them? Your own music files are never touched.\n\n" +
                "MusicBee will list the deleted placeholders as missing files until you remove missing files from the " +
                "library in MusicBee. Plugins can't do that part.",
                "Clean up placeholders", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return;
            var deleted = tidy.Delete(orphans);
            controller.ShowStatus($"Deleted {deleted} unused placeholder(s).");
        }

        /// <summary>Opens the search window, or brings the open one to the front. It isn't modal, so MusicBee stays usable.</summary>
        private void ShowSearch()
        {
            if (!controller.IsConnected || library == null)
            {
                ShowSettings();
                return;
            }
            if (searchWindow == null || searchWindow.IsDisposed)
            {
                searchWindow = new SearchForm(controller, library);
                searchWindow.FormClosed += (s, e) => searchWindow = null;
                searchWindow.Show(MainWindow);
            }
            else
            {
                if (searchWindow.WindowState == FormWindowState.Minimized)
                    searchWindow.WindowState = FormWindowState.Normal;
                searchWindow.Activate();
            }
        }

        private void ShowUnmatched()
        {
            if (!controller.IsConnected || playlistSync == null)
            {
                ShowSettings();
                return;
            }
            using var form = new UnmatchedTracksForm(controller, library, playlistSync);
            form.ShowDialog(MainWindow);
        }

        private async void SyncNow()
        {
            if (playlistSync == null || !controller.IsConnected)
            {
                controller.ShowStatus("Connect to Spotify to sync playlists.");
                return;
            }
            if (controller.Store.Playlists.Count == 0)
            {
                controller.ShowStatus("No playlists are linked yet. Import from Spotify or send one to Spotify first.");
                return;
            }
            UpgradeAddedFiles(now: true);
            controller.ShowStatus("Syncing playlists…");
            var outcomes = await playlistSync.SyncAllAsync();
            if (outcomes == null)
                controller.ShowStatus("A playlist sync is already running.");
            else if (!outcomes.Any(o => o.Changed || o.Unlinked))
                controller.ShowStatus($"Playlists are up to date ({outcomes.Count} linked).");
        }

        private IWin32Window MainWindow => Control.FromHandle(mbApiInterface.MB_GetWindowHandle());
    }
}
