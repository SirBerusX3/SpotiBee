using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using SpotiBee;
using SpotiBee.Library;
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
            about.VersionMinor = 1;
            about.Revision = 0;
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents;
            about.ConfigurationPanelHeight = 0;     // we show our own settings window

            storageDir = Path.Combine(mbApiInterface.Setting_GetPersistentStoragePath(), PluginName);
            controller = new SpotiBeeController(Path.Combine(storageDir, "settings.json"));
            controller.StatusMessage += message => mbApiInterface.MB_Trace?.Invoke("SpotiBee: " + message);
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
            controller?.Dispose();
            controller = null;
        }

        public void Uninstall()
        {
            controller?.Dispose();
            controller = null;
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
                    AddMenuItems();
                    controller.Start();
                    break;
            }
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
            return new List<ToolStripItem>
            {
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
