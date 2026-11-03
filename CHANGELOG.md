# Changelog

All notable changes to Mighty Radio. Dates are UK time.

## 1.1.0 – 2026-11-03

### New
- **Themes**: *Brass & Amber* (gunmetal and brass with an amber display, the new default) and *Phosphor*
  (green phosphor display), alongside the original *Dark*. Switch at any time from the top bar or Settings;
  the choice is saved in `settings.json`.
- **Wide scroll bars**: 48 px with large thumbs in every theme.
- **Now-playing history**: titles with station and time, saved to `history.json` (last 1,000). Copy, search
  the web, remove or clear.
- **Sleep timer**: 15/30/45/60/90/120 minutes or a custom time; fades out over the last minute, then stops.
- **Scheduled recording**: station, day, start time, length, and repeat (once, daily, weekdays), saved to
  `schedules.json`. Runs while the app is open, including from the notification area. The next recording is
  shown in the display and footer. Retries automatically if the stream drops.
- **Media keys and Windows media overlay**: Play/Pause, Stop, Next/Previous favourite.
- **Notification-area icon and mini player**: optional minimise-to-tray; closing while recordings are
  scheduled keeps the app running in the tray (can be turned off).
- **Recording notice**: the first time you record (or schedule a recording) a notice explains copyright and
  UK Wireless Telegraphy Act responsibilities and the no-warranty terms. Accepting it is stored in
  `recording-consent.json`; it can be read again from Settings.
- **Settings tab**: theme, tray options, recording notice, data and recordings folders, version information.

### Improved
- Recordings are an exact copy of the stream (no re-encoding). The file extension follows the actual audio
  (`.mp3`, `.aac`, `.ogg`, `.opus`, `.flac`); HLS streams are saved as `.ts`. Playlist (`.pls`/`.m3u`)
  stations now record correctly, and a recording that captures nothing is reported after 15 seconds.
- Recordings go to the real Music library folder, even if it has been moved to OneDrive or another drive.
- Dropped streams reconnect automatically (three tries: after 2, 5 and 10 seconds).
- Radio Browser: servers are discovered as the API asks, responses are compressed, the User-Agent names the
  project page, and a station "click" is reported once per session.
- Keyboard focus is visible on every control; screen readers announce status messages and icon-only buttons.
- Starting a second copy brings the running window to the front.
- Smaller download: the bundled VLC engine contains only the audio plugins (about 88 MB zipped, down from
  about 114 MB). The LGPL-2.1 licence text is now included in the zip.
- Closing is instant: the window hides while playback and recordings finish in the background.

### Fixed
- A `null` entry in the scanner list could leave an invisible process that blocked further launches.
- Only `http` and `https` stream addresses are played (no `file:`, `smb:`, capture devices, etc.).
- Quickly switching stations during a recording could end on the wrong station.
- Events from a station you had just left could stop the new one with an error.
- A briefly unreadable favourites file could be wiped on exit; unreadable files are now kept as
  `.bad-<date>-<time>` copies and never overwritten automatically.
- Scanner feeds that share a URL in different categories are now separate favourites.
- Repeated errors no longer show an endless series of message boxes; the log is rotated instead of deleted,
  and stream addresses are logged without query strings.

## 1.0.0

- First public release: Radio Browser search, scanner feeds, favourites, now playing, recording.
