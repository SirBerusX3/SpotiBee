# SpotiBee

A MusicBee plugin that bridges MusicBee and Spotify.

**Status: phase 1**: Spotify login, now-playing panel, transport controls, device switching.

Planned:
1. ~~Connect + now playing + controls~~
2. Import Spotify playlists into MusicBee as placeholder tracks
3. Play placeholder tracks through Spotify, kept in sync with MusicBee's player
4. Lyrics for Spotify tracks via MusicBee's lyrics system
5. Two-way playlist sync, search, "add to playlist" from the panel

Audio always plays through a Spotify client (desktop app, web player, phone or speaker).
SpotiBee is a remote control; it never streams Spotify audio itself.

## Requirements

- MusicBee 3.x (tested against the 3.6 plugin API)
- Spotify **Premium**. Spotify's API refuses playback control for free accounts.
- To build: .NET SDK 6+ (the project targets .NET Framework 4.8, the same as MusicBee)

## Build

```sh
cd src/SpotiBee
dotnet build -c Release
```

Output: `src/SpotiBee/bin/Release/net48/mb_SpotiBee.dll`

To build and copy straight into MusicBee's plugin folder (close MusicBee first, or the DLL is locked):

```sh
dotnet build -c Release -p:DeployToMusicBee=true
```

That copies to `%APPDATA%\MusicBee\Plugins`. Override with `-p:MusicBeePluginsDir=...`.

## First-time setup

1. Go to the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard) and create an app.
   - Tick **Web API**.
   - Redirect URI: `http://127.0.0.1:5543/callback` (exactly this).
2. Copy the app's **Client ID**. No client secret is needed; SpotiBee uses PKCE.
3. In MusicBee: **Edit → Preferences → Plugins**, enable **SpotiBee**, then click **Configure**
   (or use **Tools → SpotiBee Settings…**).
4. Paste the Client ID, click **Connect**, and approve access in the browser.
5. Add the panel: **View → Arrange Panels**, then drag **SpotiBee** into a panel slot.

Optional: assign hotkeys under **Preferences → Hotkeys** (search for "SpotiBee").

## Where things are stored

`%APPDATA%\MusicBee\SpotiBee\settings.json` holds the Client ID, preferred device and the
refresh token. The token is encrypted with Windows DPAPI for your Windows user account.
Disconnecting removes it; uninstalling the plugin from MusicBee deletes the folder.

## Troubleshooting

- **"Spotify Premium is required"**: the connected account is on the free tier.
- **"No active Spotify device"**: open Spotify somewhere, or pick a device from the panel's
  device dropdown. Pressing play with nothing active wakes your last-used or preferred device.
- **Login page says "INVALID_CLIENT: Invalid redirect URI"**: the dashboard's redirect URI
  must match `http://127.0.0.1:5543/callback` exactly.
- **Port 5543 in use**: another app is holding the callback port; close it and retry.
- Status and error messages appear at the bottom of the panel and are also written to
  MusicBee's trace output (`MB_Trace`), prefixed with `SpotiBee:`.

## Project layout

```
src/SpotiBee/
  Plugin.cs               MusicBee entry points (Initialise, panel, menus, notifications)
  SpotiBeeController.cs   Connection lifecycle + playback commands
  PluginSettings.cs       Persisted settings (DPAPI-protected refresh token)
  Spotify/                Web API client, PKCE auth, playback polling, JSON models
  UI/                     Dockable now-playing panel, settings window, skin colours
  MusicBeeInterface.cs    MusicBee plugin API (copied unmodified from the SDK)
MusicBee plugin API/      Original MusicBee SDK samples, for reference
```

Only .NET Framework assemblies are used (no Newtonsoft etc.), so SpotiBee can't clash with
DLL versions loaded by other MusicBee plugins.
