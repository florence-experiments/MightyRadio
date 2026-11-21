# Mighty Radio

A small, no-nonsense Windows internet radio and "scanner" player. Search thousands of stations from the
free [radio-browser.info](https://www.radio-browser.info) directory, listen to air-traffic, marine, space,
weather, ham and shortwave feeds, keep favourites, and record what you hear. Playback uses VLC's engine
(LibVLC), so almost any stream format works.

- Windows 10/11 x64, .NET 8 WPF
- Three themes: **Brass & Amber** (default), **Phosphor** and **Dark**
- No account, no ads, no telemetry

## Mighty TV

A first slice of a sister app for live TV streams is in [`MightyTV/`](MightyTV/README.md).
Channel models, the iptv-org download and the name/country/category filters live in
[`Mighty.Directory/`](Mighty.Directory/), a plain .NET 8 library with no WPF types, so a later
Android (MAUI) app or a merged radio and TV app can reference it. Mighty Radio itself is unchanged.

## Features

- **Radio stations**: search by name, country (United Kingdom by default) and genre. Sort by popularity, plays, trend or name. Double-click to play.
- **Scanner tab**: air traffic, marine, space, public safety, amateur/ham, weather and shortwave/utility feeds.
  - Items marked "opens in browser" open the provider's web page instead of playing in the app.
  - The list is a plain JSON file you can edit (*Edit list* opens it in your editor; *Reload* applies changes).
- **Favourites**: star any station or feed. Reorder with ▲/▼.
- **Now playing**: shows the stream's song or programme title when the station sends one (ICY metadata), and
  reconnects automatically (up to three tries) if the stream drops.
- **History**: every title you hear is kept with the station and time (the last 1,000). Copy lines, look a
  title up on the web, remove lines or clear the list.
- **Recording**: saves an exact copy of the stream (no re-encoding) to your Music library's `MightyRadio`
  folder. The file type follows what the stream really contains: usually `.mp3`, `.aac`, `.ogg`, `.opus` or `.flac`, and `.ts` for HLS streams.
- **Scheduled recording**: record a favourite (or the current station) at a set time for a set number of
  minutes, once, every day or on weekdays. Recordings run while Mighty Radio is open, including when it is
  minimised to the notification area. The next one is shown in the display and the footer.
- **Sleep timer**: 15 min to 2 hours, or any number of minutes. The sound fades out over the last minute, then stops.
- **Media keys**: Play/Pause, Stop, Next and Previous (Next/Previous step through your favourites). The station
  and title appear in the Windows media overlay.
- **Mini player and tray**: a compact always-on-top player, and an option to minimise to the notification area.
- **Accessible**: wide 48 px scroll bars in every theme, visible keyboard focus, and screen-reader names and announcements.
- Copes with `.pls`/`.m3u` playlist URLs and HLS streams. Only `http` and `https` addresses are ever played.

## Themes

Choose a theme from the drop-down at the top right, or under **Settings**. It changes immediately and is remembered.

| Theme | Look |
|---|---|
| Brass & Amber | Gunmetal panels, brass edges, an amber display with a soft glow |
| Phosphor | Near-black panels with a green phosphor display, like an old terminal |
| Dark | The original dark look with rounded corners |

## Download and run

1. Download `MightyRadio-v1.1.1-win-x64.zip` from the [Releases](https://github.com/florence-experiments/MightyRadio/releases) page and unzip it.
2. Keep the whole `MightyRadio` folder together. Do not move `MightyRadio.exe` out of it, because it needs the `libvlc` folder next to it.
3. Double-click `MightyRadio.exe`.
4. The first time, Windows may show "Windows protected your PC" (SmartScreen) because the app isn't code-signed. Click **More info**, then **Run anyway**.
5. Optional: right-click `MightyRadio.exe` > Show more options > Send to > Desktop (create shortcut).

No installer and no admin rights are needed. The release is self-contained, so you don't need to install .NET.
The v1.1 zip is about 88 MB (v1.0 was about 114 MB): the bundled VLC engine now contains only the audio parts.

## Using it

- **Radio stations**: enter part of a name (e.g. `BBC`), pick a country or *Any country*, optionally a genre (`jazz`, `news`, `classical`…), then press **Search** or Enter.
- **Scanner**: filter by text or category. **Play / open** either plays the feed or opens its web page.
- **Favourites**: use **☆ Add to favourites** on any tab. Remove with **✕ Remove** or the Delete key.
- **Record**: while a station is playing, press **Record**. Press it again (or **Stop**) to finish. The first
  time, a short notice explains your responsibilities (see the disclaimer below); tick the box and press **Continue**.
- **Schedule**: pick a station, day, start time (24-hour, e.g. `19:00`), length in minutes and repeat, then
  press **Add**. Keep Mighty Radio open (minimised is fine) and the PC awake. Closing the window while
  recordings are scheduled keeps it running in the notification area (you can turn this off in Settings); use
  the tray icon's **Exit** to quit completely.
- **Sleep timer**: choose a time from the *Sleep* drop-down next to the Record button. Choose *Sleep: off* or press Stop to cancel.

## Where things are saved

| Path | What |
|---|---|
| `%APPDATA%\MightyRadio\favourites.json` | your favourites |
| `%APPDATA%\MightyRadio\settings.json` | volume, theme, last station, window size, country, sort order, tray options |
| `%APPDATA%\MightyRadio\history.json` | now-playing history (last 1,000 titles) |
| `%APPDATA%\MightyRadio\schedules.json` | scheduled recordings |
| `%APPDATA%\MightyRadio\recording-consent.json` | when you accepted the recording notice, and in which app version |
| `%APPDATA%\MightyRadio\stations-scanner.json` | your editable scanner list (copied from the bundled one on first run) |
| `%APPDATA%\MightyRadio\error.log` | only written if something goes wrong (stream addresses are logged without their query strings) |
| *Music library*`\MightyRadio\` | recordings (follows the Music folder even if it has been moved to OneDrive or another drive) |

If a file can't be read, Mighty Radio keeps a copy named `<file>.bad-<date>-<time>` and does not overwrite the
original until you change that data. To reset the app, close it and delete `%APPDATA%\MightyRadio`.

## Privacy

Mighty Radio only contacts:

- the radio-browser.info API, to find a server, search stations, list countries, and register a "click" for a
  station you play (once per station per session), which is how the directory ranks popular stations;
- the stream or web page you choose to open.

There is no telemetry, analytics or crash reporting. Requests identify themselves as
`MightyRadio/<version> (+https://github.com/florence-experiments/MightyRadio)`. `error.log` stays on your PC.

## Disclaimer: recording and scanner feeds

**You are responsible for how you use Mighty Radio.** Before recording, make sure you are allowed to:

- Radio programmes and music are usually protected by copyright. Recording for your own private listening
  later may be allowed where you live; sharing, publishing or selling recordings usually is not.
- Some scanner entries relay radio traffic that is not intended for the general public, such as air traffic
  control, marine VHF and overseas emergency services. They are third-party internet streams; Mighty Radio does
  not receive anything over the air. The rules on listening to, recording and sharing such traffic differ by
  country. In the UK, section 48 of the Wireless Telegraphy Act 2006 makes it an offence, without authority, to
  obtain information from non-broadcast transmissions or to disclose it; see Ofcom's guidance. Please check what
  applies where you live. Links to LiveATC.net and Broadcastify open on their own websites, under their terms.

Mighty Radio is provided **as is, without warranty of any kind**, under the MIT licence. The author accepts no
liability for how it is used or for any recording made with it. The first time you press **Record** (or add a
scheduled recording) the app shows this notice and asks you to confirm that you understand and accept it. You
can read it again any time under **Settings > View recording notice**.

## Building from source

Requirements: the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Windows 10 1809 or later is
needed to run the app. Building on Linux or macOS also works because the project sets `EnableWindowsTargeting`.

```powershell
git clone https://github.com/florence-experiments/MightyRadio.git
cd MightyRadio
dotnet build MightyRadio/MightyRadio.csproj -c Release

# Self-contained x64 build (what the release zip contains; README.txt, licences and notices are copied automatically):
dotnet publish MightyRadio/MightyRadio.csproj -c Release -r win-x64 --self-contained -o publish/MightyRadio
```

The VLC engine comes from the `VideoLAN.LibVLC.Windows` NuGet package and is copied to
`publish/MightyRadio/libvlc/win-x64/`. Only the audio plugins listed in `MightyRadio.csproj`
(`VlcWindowsX64IncludeFiles`) are included; delete that `ItemGroup` to ship the full engine.

Before changing any XAML, run `python3 tools/check-xaml-resources.py`. It finds resource mistakes that compile
but break at runtime (a missing key, or a number where a grid length, thickness or brush is needed). Builds on
Linux and macOS run it automatically; skip it with `-p:SkipXamlCheck=true`.

Project layout:

```
MightyRadio/
  App.xaml(.cs)              start-up, single instance, theme, VLC initialisation, crash logging, shutdown
  MainWindow.xaml(.cs)       the UI (tabs: Radio stations, Scanner, Favourites, History, Schedule, Settings)
  Models/                    Station, ScannerFeed, settings
  Services/
    AppInfo.cs               version, User-Agent, shared HttpClient, http/https URL check
    RadioBrowserClient.cs    radio-browser.info API client (server discovery)
    RadioPlayer.cs           LibVLC playback wrapper (threading rules documented in the file), auto-reconnect
    Recorder.cs              lossless recording (stream dump, or TS remux for HLS)
    Scheduler.cs             scheduled recordings
    HistoryStore.cs          now-playing history
    MediaControls.cs         media keys and the Windows media overlay (SMTC)
    TrayIcon.cs              notification-area icon (Win32 Shell_NotifyIcon)
    RecordingConsent.cs      the recording notice text and acceptance file
    ThemeManager.cs          theme switching (each theme is checked off-screen first)
    ScannerRepository.cs     loads stations-scanner.json
    Storage.cs               %APPDATA% JSON files, Music folder and error log
  Themes/                    Brass.xaml, Phosphor.xaml, Dark.xaml (colours) and Controls.xaml (control styles)
  Views/                     RecordingNoticeWindow
  stations-scanner.json      bundled scanner feed list
docs/
  IMPROVEMENTS.md            code review findings and roadmap
  README.txt                 the plain-text readme shipped inside the release zip
licenses/
  LGPL-2.1.txt               shipped in the release zip for LibVLC and LibVLCSharp
```

## Contributing

See [docs/IMPROVEMENTS.md](docs/IMPROVEMENTS.md) for known issues and planned features, and
[CHANGELOG.md](CHANGELOG.md) for what changed. Feed additions to `stations-scanner.json` are welcome. Please
prefer official broadcaster streams, and use `"type": "web"` for services that don't allow embedding.

## Credits and licences

- [VLC / LibVLC](https://www.videolan.org/vlc/libvlc.html) by VideoLAN: the playback engine (LGPL-2.1-or-later).
- [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp): .NET bindings for LibVLC (LGPL-2.1-or-later).
- [Radio Browser](https://www.radio-browser.info): the free, community-run station directory.
- Scanner feeds are provided by their respective operators (SomaFM, wxradio.org, LiveATC.net, Broadcastify, University of Twente WebSDR and others).

Mighty Radio's own code is released under the [MIT licence](LICENSE). Bundled components keep their own
licences; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
