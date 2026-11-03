# Mighty Radio

A small, no-nonsense Windows internet radio and "scanner" player. Search thousands of stations from the
free [radio-browser.info](https://www.radio-browser.info) directory, listen to air-traffic, marine, space,
weather, ham and shortwave feeds, keep favourites, and record what you hear. Playback uses VLC's engine
(LibVLC), so almost any stream format works.

- Windows 10/11 x64, .NET 8 WPF, dark UI
- No account, no ads, no telemetry

## Features

- **Radio stations**: search by name, country (United Kingdom by default) and genre. Sort by popularity, plays, trend or name. Double-click to play.
- **Scanner tab**: air traffic, marine, space, public safety, amateur/ham, weather and shortwave/utility feeds.
  - Items marked "opens in browser" open the provider's web page instead of playing in the app.
  - The list is a plain JSON file you can edit (*Edit list* opens it in Notepad; *Reload* applies changes).
- **Favourites**: star any station or feed. Reorder with ▲/▼.
- **Now playing**: shows the stream's song or programme title when the station sends one (ICY metadata).
- **Recording**: saves to `Music\MightyRadio`. MP3 streams are copied as-is (`.mp3`); other formats are saved as Ogg Vorbis (`.ogg`).
- Copes with `.pls`/`.m3u` playlist URLs and HLS streams. Shows a friendly message when a station is off air.

## Download and run

1. Download `MightyRadio-win-x64.zip` (or `.7z`) from the Releases page and unzip it.
2. Keep the whole `MightyRadio` folder together. Do not move `MightyRadio.exe` out of it, because it needs the `libvlc` folder next to it.
3. Double-click `MightyRadio.exe`.
4. The first time, Windows may show "Windows protected your PC" (SmartScreen) because the app isn't code-signed. Click **More info**, then **Run anyway**.
5. Optional: right-click `MightyRadio.exe` > Show more options > Send to > Desktop (create shortcut).

No installer and no admin rights are needed. The release is self-contained, so you don't need to install .NET.

## Using it

- **Radio stations**: enter part of a name (e.g. `BBC`), pick a country or *Any country*, optionally a genre (`jazz`, `news`, `classical`…), then press **Search** or Enter.
- **Scanner**: filter by text or category. **Play / open** either plays the feed or opens its web page.
- **Favourites**: use **☆ Add to favourites** on any tab. Remove with **✕ Remove** or the Delete key.
- **Record**: while a station is playing, press **Record**. Press it again (or **Stop**) to finish.

## Where things are saved

| Path | What |
|---|---|
| `%APPDATA%\MightyRadio\favourites.json` | your favourites |
| `%APPDATA%\MightyRadio\settings.json` | volume, last station, window size, country, sort order |
| `%APPDATA%\MightyRadio\stations-scanner.json` | your editable scanner list (copied from the bundled one on first run) |
| `%APPDATA%\MightyRadio\error.log` | only written if something goes wrong |
| `%USERPROFILE%\Music\MightyRadio\` | recordings |

To reset the app, close it and delete `%APPDATA%\MightyRadio`.

## Privacy

Mighty Radio only contacts:

- the radio-browser.info API, to search stations, list countries, and register a "click" for a station you play, which is how the directory ranks popular stations;
- the stream or web page you choose to open.

There is no telemetry, analytics or crash reporting. `error.log` stays on your PC.

## A note on scanner feeds

Some scanner entries relay radio traffic that is not intended for the general public, such as air traffic
control, marine VHF and overseas emergency services. They are third-party internet streams; Mighty Radio does
not receive anything over the air. The rules on listening to, recording and sharing such traffic differ by
country. In the UK, see the Wireless Telegraphy Act 2006 and Ofcom guidance. Please check what applies where
you live. Links to LiveATC.net and Broadcastify open on their own websites, under their terms.

## Building from source

Requirements: the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Windows is needed to run
the app. Building on Linux or macOS also works because the project sets `EnableWindowsTargeting`.

```powershell
git clone https://github.com/<owner>/mighty-radio.git
cd mighty-radio
dotnet build MightyRadio/MightyRadio.csproj -c Release

# Self-contained x64 build (what the release zip contains):
dotnet publish MightyRadio/MightyRadio.csproj -c Release -r win-x64 --self-contained -o publish/MightyRadio
copy docs\README.txt publish\MightyRadio\
```

The VLC engine comes from the `VideoLAN.LibVLC.Windows` NuGet package and is copied to
`publish/MightyRadio/libvlc/win-x64/` automatically.

Project layout:

```
MightyRadio/
  App.xaml(.cs)            start-up, single instance, VLC initialisation, crash logging
  MainWindow.xaml(.cs)     the UI (tabs: Radio stations, Scanner, Favourites)
  Models/                  Station, ScannerFeed, settings
  Services/
    RadioBrowserClient.cs  radio-browser.info API client
    RadioPlayer.cs         LibVLC playback wrapper (threading rules documented in the file)
    Recorder.cs            recording via a VLC stream-output chain
    ScannerRepository.cs   loads stations-scanner.json
    Storage.cs             %APPDATA% JSON files and error log
  Themes/Dark.xaml         colours and control styles
  stations-scanner.json    bundled scanner feed list
docs/
  IMPROVEMENTS.md          code review findings and roadmap
  README.txt               the plain-text readme shipped inside the release zip
```

## Contributing

See [docs/IMPROVEMENTS.md](docs/IMPROVEMENTS.md) for known issues and planned features. Feed additions to
`stations-scanner.json` are welcome. Please prefer official broadcaster streams, and use `"type": "web"` for
services that don't allow embedding.

## Credits and licences

- [VLC / LibVLC](https://www.videolan.org/vlc/libvlc.html) by VideoLAN: the playback engine (LGPL-2.1-or-later).
- [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp): .NET bindings for LibVLC (LGPL-2.1-or-later).
- [Radio Browser](https://www.radio-browser.info): the free, community-run station directory.
- Scanner feeds are provided by their respective operators (SomaFM, wxradio.org, LiveATC.net, Broadcastify, University of Twente WebSDR and others).

Mighty Radio's own code is released under the [MIT licence](LICENSE). Bundled components keep their own
licences; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
