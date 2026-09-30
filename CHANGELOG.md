# Changelog

All notable changes to SpotiBee are listed here, newest first.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/). Before 1.0, minor versions may change behaviour.

## [Unreleased] – 0.4.0

### Added
- **Two-way playlist sync.** Imported playlists, and playlists sent from MusicBee, stay linked.
  - Changes in MusicBee (add, remove, reorder) reach Spotify a few seconds later.
  - Changes made in Spotify, on any device, are picked up within about 3 minutes.
  - When both sides changed, the edits are merged: removals from either side apply, and
    additions from both are kept.
- **Send Playlists to Spotify** (Tools menu): creates a private Spotify playlist from a MusicBee
  playlist, even one made entirely from local files.
  - Each local file is matched to Spotify's catalogue by title and artist, and the length must
    agree within 4 seconds.
  - Tracks Spotify doesn't have stay in the MusicBee playlist in their original place, and
    are listed at the end.
- **Liked Songs sync**: adding to or removing from the MusicBee copy likes or unlikes the song on
  Spotify.
- **Sync Playlists Now** command (Tools menu, panel header menu, assignable hotkey).
- Setting to turn automatic sync off.

### Changed
- Spotify playlists are updated with individual adds and removes, so each song keeps its "date
  added". The whole list is only rewritten when the order changes, and never when the playlist
  contains podcast episodes or Spotify local files, since a rewrite would delete them.
- Re-importing an already linked playlist now syncs it instead of overwriting MusicBee-side edits.
- Auto-playlists sync one way only (MusicBee → Spotify).
- Deleting a playlist on one side unlinks it and leaves the other side untouched.
- Local files that Spotify doesn't have aren't searched for again for a week.

## [0.3.1] – 2026-09-30

### Fixed
- **Blank SpotiBee panel after restarting MusicBee.** When MusicBee restores a saved layout, it
  asks for the panel from a background thread. The panel is now always built on MusicBee's UI
  thread.
- **Being signed out of Last.fm.** SpotiBee no longer touches MusicBee's Scrobble toggle, which
  turned out to sign you out of Last.fm. MusicBee now scrobbles placeholder tracks like any
  other track. In testing, Last.fm counted a play only once even with both MusicBee and Spotify
  linked to it.

### Added
- **Save Diagnostics Report** (Tools menu), which writes connection, playback and panel state
  plus recent log lines to a text file.
- The panel shows an error message instead of staying blank if it fails to load.

### Removed
- The "Don't let MusicBee scrobble tracks that play through Spotify" setting (see above).

## [0.3.0] – 2026-09-30

### Added
- **Play Spotify tracks from MusicBee's queue.** For each track, SpotiBee decides whether MusicBee
  or Spotify makes the sound, based on the playback mode:
  - **Local first** (default): your files play in MusicBee, Spotify-only tracks play through
    Spotify.
  - **Spotify first**: everything Spotify has plays through Spotify, and your files are the
    fallback.
  - **Local only**: Spotify is never used, and Spotify-only tracks are skipped.
- While Spotify plays, MusicBee plays the placeholder silently underneath, so the queue, progress
  bar and play counts work as normal.
- Pause, resume and seek are mirrored both ways, and any drift is corrected.
- **Volume is linked**: MusicBee's slider controls Spotify's volume, and changes in the Spotify
  app move the slider.
- **Hand-over**: picking something else in the Spotify app pauses MusicBee. Pressing play in
  MusicBee takes control back.
- **Fallback**: if Spotify drops out (connection lost, device gone, playback stalled), a local
  track carries on from the same point and a Spotify-only track is skipped. Spotify is retried
  after a minute.
- A mode button and a "MusicBee → Spotify" indicator in the panel, a playback mode menu in the
  panel header, and a "Switch Playback Mode" hotkey.
- If MusicBee closes unexpectedly, a mute SpotiBee applied is undone at the next start.

### Changed
- While MusicBee is driving Spotify, the panel's play/pause, next, previous and seek controls act
  on MusicBee's queue.
- Spotify's repeat is turned off while MusicBee is driving it, so a single track doesn't loop.

## [0.2.0] – 2026-09-30

### Added
- **Import Spotify playlists** and Liked Songs into a **Spotify** playlist folder in MusicBee.
  - Songs you already have locally use your own file, matched by artist and title with the
    length confirmed within 4 seconds.
  - Everything else becomes a **placeholder**: a tiny silent Ogg Opus file (about 6 KB plus
    artwork) tagged with the Spotify track's title, artists, album, artwork, track number and
    ISRC. MusicBee treats placeholders as normal tracks, including lyrics lookup.
- Placeholders are added to the library under `Music\SpotiBee\Artist\Album\` (the folder can be
  changed in settings). To filter them, use *Encoder is SpotiBee*.
- Links between Spotify tracks and files are kept in SpotiBee's own data file, so your music
  files' tags are never modified.
- Supports Spotify's February 2026 API changes (the `/playlists/{id}/items` endpoints).
  Playlists Spotify no longer lets apps read (ones you don't own or collaborate on) are shown
  greyed out.

### Fixed
- A false "this account isn't Premium" warning. Spotify stopped reporting account type to apps
  in February 2026, so SpotiBee now relies on the Premium error from actual playback commands.

## [0.1.0] – 2026-09-30

### Added
- **Spotify login** from MusicBee using your own Spotify developer app's Client ID. There's no
  client secret: login uses PKCE with a local `127.0.0.1` callback. The refresh token is
  encrypted with Windows DPAPI for your user account.
- **Dockable now-playing panel** that follows your MusicBee skin: artwork, title, artist and
  album; a seek bar you can click or drag; shuffle, previous, play/pause, next and repeat; and a
  device picker for switching Spotify Connect devices.
- Pressing play with no active Spotify device wakes your preferred or last-used device.
- Tools menu entries, plus assignable hotkeys for play/pause, next and previous.
- Follows playback started elsewhere (Spotify app, phone, speakers) by checking Spotify every
  few seconds.
- No third-party DLLs, only .NET Framework assemblies, so SpotiBee can't clash with other
  MusicBee plugins.

[Unreleased]: #unreleased--040
[0.3.1]: #031--2026-09-30
[0.3.0]: #030--2026-09-30
[0.2.0]: #020--2026-09-30
[0.1.0]: #010--2026-09-30
