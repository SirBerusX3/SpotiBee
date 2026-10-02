# Changelog

All notable changes to SpotiBee are listed here, newest first.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/). Before 1.0, minor versions may change behaviour.

## [Unreleased]

### Added
- **Like, playlist, album and artist buttons on the panel** for the song Spotify is playing:
  - **♥** likes or unlikes it, and shows whether it's already in your Liked Songs.
  - **+** lists the Spotify playlists you can edit, with their lengths. Ticked playlists already
    have the song; click one to add it or remove it.
  - **…** saves or removes the album, follows or unfollows each artist on the song, and opens
    the album or artist in Spotify.
- **SpotiBee in MusicBee's right-click menu** for the selected songs, your own files included
  (they're matched to Spotify first): like, add to or remove from Spotify playlists, save the
  album, follow the artist, open in Spotify.
- When one of those changes a synced playlist or Liked Songs, the MusicBee copy catches up a
  couple of seconds later instead of at the next sync. Liking from Search does the same.
- What's in each playlist is remembered between sessions and only fetched again for playlists
  that changed on Spotify.

### Changed
- SpotiBee now asks Spotify for permission to see and change the artists you follow. Existing
  logins keep working; the first time you follow an artist, SpotiBee offers to reconnect so you
  can approve it.

## [0.5.0] – 2026-10-01

### Added
- **Search Spotify** from a new search button on the panel, the Tools menu, or a hotkey. The
  window stays open alongside MusicBee.
  - Select one or more results and play now, play next, add to the queue, add to any MusicBee
    playlist, or like on Spotify.
  - Songs you own use your own file; others get a placeholder on the spot.
  - Adding to a synced playlist reaches Spotify through the normal sync.
  - **Search in** Everything, Artist, Song or Album; the choice is remembered.
- **Your own copy replaces placeholders**: when you add a song to your library that you only had
  as a placeholder (same title, artist and length), your file is swapped into every playlist
  that used the placeholder. SpotiBee checks the whole library shortly after MusicBee starts,
  after files are added, and before Sync Playlists Now, rather than relying on MusicBee's
  "file added" notification, which isn't sent for every way of adding files.
- **Clean Up Unused Placeholders** (Tools menu): deletes placeholders no playlist uses any more,
  after asking. Your own files are never touched.
- **Lyrics in the panel** for whatever Spotify plays outside MusicBee.
  - Synced lyrics highlight and follow the current line.
  - Lyrics come from your saved lyrics first, then LRCLIB. When saved lyrics are plain text,
    LRCLIB's synced version is preferred if it has one.
  - Lyrics found for placeholders (including ones MusicBee downloads) are saved into them for next
    time.
  - Toggle with **Show lyrics** in the panel header menu.

### Changed
- The SpotiBee panel is now resizable, so lyrics can use any extra height.
- Matching local files to Spotify tracks (on import and when upgrading placeholders) accepts
  close-length edits, "50th Anniversary Edition"-style labels, dropped g's ("Losin'"/"Losing"),
  and titles a letter apart when the length matches to the second ("Key"/"Keys To Your Love").

## [0.4.0] – 2026-09-30

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
- **Tracks Not Found on Spotify** window: pick the Spotify version of a local track by hand from
  a search. Other songs in the results are greyed out; of the same song, the closest length to
  your file is preselected. Hand-picked matches are added to every synced playlist containing the file, and
  automatic matching never overrides them.
- Setting to turn automatic sync off.

### Changed
- Spotify playlists are updated with individual adds and removes, so each song keeps its "date
  added". The whole list is only rewritten when the order changes, and never when the playlist
  contains podcast episodes or Spotify local files, since a rewrite would delete them.
- Re-importing an already linked playlist now syncs it instead of overwriting MusicBee-side edits.
- Auto-playlists sync one way only (MusicBee → Spotify).
- Deleting a playlist on one side unlinks it and leaves the other side untouched.
- The version MusicBee shows for the plugin now always matches the release (it was stuck at 0.3).
- Local files that Spotify doesn't have aren't searched for again for a week, or until matching
  improves in an update.
- **Better matching of local files to Spotify.** In testing, this recovers most of the tracks
  missed in a first bulk send of compilation playlists.
  - Release labels are ignored, including nested ones: "(Album Version (Explicit))", "(Single
    Version / Mono)", "(Mono Single Master)", " - 2009 Remaster". Remixes, live, acoustic and
    extended versions still only match themselves.
  - Album prefixes in titles ("Led Zeppelin II - Whole Lotta Love") and artist nicknames
    (Charlie "Bird" Parker, UGK (Underground Kingz)) are handled.
  - Band names with "&" are searched in full ("Sly & The Family Stone"), and name variants such
    as "Joe Turner" / "Big Joe Turner" are accepted.
  - A close-length version (within 25 s or 12%) is accepted when no exact one exists. In Spotify
    first mode, those files play locally, since they can't keep time with Spotify's edit.
  - Tracks that still don't match are logged with what Spotify returned.

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

[Unreleased]: #unreleased
[0.5.0]: #050--2026-10-01
[0.4.0]: #040--2026-09-30
[0.3.1]: #031--2026-09-30
[0.3.0]: #030--2026-09-30
[0.2.0]: #020--2026-09-30
[0.1.0]: #010--2026-09-30
