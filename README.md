# SpotiBee

A MusicBee plugin that bridges MusicBee and Spotify.

**Status: phase 2**: Spotify login, now-playing panel, controls, and playlist import.

Planned:
1. ~~Connect + now playing + controls~~
2. ~~Import Spotify playlists into MusicBee as placeholder tracks~~
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

## Importing playlists

**Tools → SpotiBee: Import Spotify Playlists…** (or the panel header menu). Tick playlists and
click Import. Each one becomes a MusicBee playlist in a **Spotify** playlist folder:

- Songs you already have locally use **your own file**. They're matched by artist and title,
  and the length has to agree within 4 seconds, so live versions and edits don't get mixed up.
- Everything else gets a **placeholder**: a tiny silent Opus file (about 6 KB plus artwork)
  tagged with the Spotify track's details. Placeholders are added to your library and stored in
  `Music\SpotiBee\Artist\Album\`.
- Re-importing updates the same MusicBee playlist in place. Previously imported playlists are
  ticked automatically, so importing again refreshes them all.

To hide placeholders from a view, filter on **Encoder is SpotiBee**, or on the path containing
`\Music\SpotiBee\`.

Spotify limits (Development Mode apps, since February 2026):
- Only playlists you **own or collaborate on** can be imported. To import someone else's
  playlist, copy its songs into a playlist of your own in Spotify first.
- Liked Songs can be imported.
- Podcast episodes and Spotify "local files" without a match in your library are skipped.

Placeholders don't produce sound yet. Playing them through Spotify is phase 3.

## Where things are stored

In `%APPDATA%\MusicBee\SpotiBee\`:
- `settings.json` holds the Client ID, preferred device, placeholder folder, and the refresh
  token. The token is encrypted with Windows DPAPI for your Windows user account.
- `library.json` links Spotify tracks to their placeholder and/or local file, and records
  which MusicBee playlist each import became. Your music files' tags are never changed.

Disconnecting removes the token; uninstalling the plugin from MusicBee deletes the folder.
Placeholder files are left alone, because they're part of your MusicBee library.

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
  Library/                Playlist import, local matching, placeholder files, track store
  UI/                     Dockable now-playing panel, settings window, skin colours
  MusicBeeInterface.cs    MusicBee plugin API (copied unmodified from the SDK)
MusicBee plugin API/      Original MusicBee SDK samples, for reference
```

Only .NET Framework assemblies are used (no Newtonsoft etc.), so SpotiBee can't clash with
DLL versions loaded by other MusicBee plugins.
