# Mighty Radio – code review and improvement plan

Reviewed: every file under `MightyRadio/` (`App.xaml(.cs)`, `MainWindow.xaml(.cs)`, `Models/`, `Services/`,
`Themes/Dark.xaml`, `stations-scanner.json`, `MightyRadio.csproj`, `app.manifest`). Line numbers are for the
version in this commit. The project builds cleanly (`dotnet build -c Release -p:EnableWindowsTargeting=true`,
0 warnings, 0 errors on .NET SDK 8.0.426). Line numbers refer to v1.0; the status of each item in v1.1.0 is
shown in its heading and summarised below.

Overall the code is in good shape: LibVLC events are marshalled to the UI thread, blocking VLC calls run off
the UI thread through a serialised task chain, settings are written atomically (`.tmp` + `File.Move`), browser
links are restricted to http/https, and failures are logged instead of crashing. The issues below are mostly
edge cases.

## Status in v1.1.0

| Item | Status |
|---|---|
| H1–H4 | Done. |
| M1, M4–M11 | Done. |
| M2 | Partly done. The recorder still opens its own connection (needed for a lossless byte copy that doesn't interrupt playback), but it now records the playlist entry that is actually playing and expands `.pls`/`.m3u` itself, and a 0-byte recording is reported after 15 s. |
| M3 | Done differently: no transcoding at all. Icecast/Shoutcast streams are saved byte-for-byte (`demux=dump`) and the file extension is chosen from the real header (`.mp3`, `.aac`, `.ogg`, `.opus`, `.flac`); HLS is remuxed to `.ts`. |
| L1–L4, L7–L9, L11–L14 | Done. |
| L5, L6, L10 | Open. |
| Ideas 1, 3, 4, 7, 8 | Done (Brass & Amber and Phosphor themes, history, sleep timer, tray and mini player, media keys + Windows media overlay). In-app keyboard shortcuts from idea 8 are still open. |
| Idea 2 | Partly: scroll bars are 48 px in every theme, but the width is not yet a setting. |
| Idea 5 | Partly: schedules run while the app is open (including minimised to the tray). No Windows Task Scheduler integration. |
| Idea 11 | Partly: auto-reconnect is done; the buffering setting is open. |
| Idea 15 | Partly: the LibVLC plugin set is trimmed to audio only, but the self-contained .NET runtime (which always bundles the full Windows Desktop runtime, WinForms included) is now the bulk of the download, so the zip is about 23% smaller rather than half. A framework-dependent build would be much smaller but needs the .NET 8 Desktop Runtime installed. |
| Ideas 6, 9, 10, 12–14, 16 | Open. |

---

## High priority

### H1. A `null` in the editable scanner list can leave an invisible "zombie" process — done in 1.1.0
`Models/ScannerFeed.cs:21` (`Name = Name.Trim()`) and `Services/ScannerRepository.cs:41`
(`f.Url` on a possibly-null element).
The scanner list is meant to be hand-edited in Notepad. System.Text.Json in .NET 8 ignores nullable
annotations, so `"name": null` (or a stray `null` entry in the `feeds` array) produces a
`NullReferenceException`. `ScannerRepository.Load()` is called from the `MainWindow` constructor
(`MainWindow.xaml.cs:82`), which runs inside `App.OnStartup`. The exception goes to
`OnDispatcherUnhandledException` (`App.xaml.cs:57`), which sets `Handled = true`, so the user sees an
"Oops" box, the window is never shown, and the process keeps running and holding the single-instance mutex.
Every later launch then says "Mighty Radio is already open" until the process is killed in Task Manager.
**Fix:**
- In `ScannerRepository.Load`, filter `f is not null` and use `(f.Name ?? "").Trim()` / `(f.Url ?? "").Trim()` in `ToStation`.
- Wrap `LoadScannerList()` in try/catch.
- In `App.OnStartup`, construct `MainWindow` inside the existing try block and call `Shutdown(1)` on failure, so a startup failure can never leave a windowless process.

### H2. Stream URLs are not restricted to network schemes — done in 1.1.0
`Services/RadioPlayer.cs:77`, `Services/ScannerRepository.cs:41`, `Services/Recorder.cs:70`.
Playback only checks `Uri.TryCreate(..., UriKind.Absolute)`, so any scheme reaches LibVLC: `file:///…`,
`smb://host/share` (on Windows this can trigger an outbound SMB/NTLM handshake to an arbitrary host),
`dshow://` (opens the microphone or capture device), `screen://`, and so on. The URLs come from Radio Browser,
which is crowd-sourced, from favourites, and from the editable scanner JSON. `OpenInBrowser` already does the
right thing (`MainWindow.xaml.cs:485`).
**Fix:** add one helper, for example `Station.TryGetPlayableUri(out Uri)`, that accepts only
`http`, `https` (and optionally `icyx`/`mms`/`rtsp` if a real feed needs them), and use it in
`RadioPlayer.Play`, `Recorder.Start` and `ScannerRepository.Load`.

### H3. Quick station changes during a recording can end on the wrong station — done in 1.1.0
`MainWindow.xaml.cs:103-125` (`async void Play`).
If a recording is running, `Play` awaits `StopRecordingAsync`, which takes up to a few seconds. A second
double-click during that await finds `IsRecording == false` (it is cleared synchronously in
`Recorder.StopAsync`), so it plays immediately. Then the first call resumes and switches back to the
**older** station.
**Fix:** keep a `_playRequest` counter. Increment it at the top of `Play`, and after the await return if the
counter has moved on. Alternatively, stop the recorder without awaiting before switching.

### H4. Player events can be attributed to the wrong station — done in 1.1.0
`Services/RadioPlayer.cs:46-48` and `:161`.
`Post` reads the generation *when the event arrives*, not when the media that raised it was current.
An `EncounteredError` or `EndReached` from the stream being torn down can therefore arrive after `Play`
has incremented `_generation`, and kill the new station with "this station isn't working".
**Fix:** capture identity at the source. Subscribe to `Media.StateChanged` per media instead of
`MediaPlayer` events, or compare `_mp.Media` / sender with `_media` inside the posted callback. Also increment
`_generation` in the connect-timeout path (`:51-58`) and in `OnError`, so a late `Playing` cannot revive a
stream that was already abandoned.

---

## Medium priority

### M1. LibVLC is never disposed, and shutdown blocks the UI for up to about 6 s — done in 1.1.0
`Services/RadioPlayer.cs:30/43` (the `LibVLC` instance is created here) and `:244-258` (`Dispose` only
disposes `_mp`). In `MainWindow.xaml.cs:540-541`, `Recorder.Dispose` and `RadioPlayer.Dispose` each block the
UI thread for up to 3 s with `.Wait(3s)`.
**Fix:** dispose `LibVlc` last, after both players, in `App.OnExit` (the recorder shares it). Hide the window
first in `OnClosing` (`Hide()`), so the blocking shutdown is invisible. If the wait times out, skip
`LibVlc.Dispose()` rather than racing a running `Stop()`.

### M2. Recording opens a second connection, and the recording can differ from what is heard — partly done in 1.1.0
`Services/Recorder.cs:70-88`.
The recorder starts a second `MediaPlayer` on `station.StreamUrl`. This has several consequences:
- It doubles bandwidth.
- Stations with connection limits refuse it, which shows up as 0-byte recordings and the message "station may not allow it".
- The file starts at a different point and may carry different pre-roll or ads.
- For `.pls`/`.m3u` URLs, the recorder never expands sub-items the way `RadioPlayer.OnEndReached` does (`RadioPlayer.cs:193-210`), so these recordings end immediately with 0 bytes.

**Fix (short term):** record the MRL that is actually playing. Expose `RadioPlayer.CurrentMrl`, updated when a
sub-item is chosen. **Fix (better):** use a single player with
`:sout=#duplicate{dst=display,dst=std{access=file,mux=…,dst="…"}}` and restart it when recording starts or
stops. This produces one connection and records exactly what is heard.

### M3. Recording format choice relies on a codec label — done in 1.1.0 (no transcoding)
`Services/Recorder.cs:36-38, 46-47`.
- AAC/HE-AAC streams, which are common on UK stations including the BBC, are transcoded to Vorbis 128k. That is a lossy-to-lossy conversion and a CPU cost.
- A station whose Radio Browser `codec` says MP3 but which actually sends AAC is written raw into a `.mp3` file that many players reject.

**Fix:** copy AAC as ADTS (`mux=raw`, `.aac`) when the codec is AAC. Better still, after `Playing`, read the
real codec from `_mp.Media.Tracks` (`AudioTrack` fourcc `mpga`/`mp4a`) instead of trusting the directory
label. Also make `IsMp3` null-safe (`s.Codec?.Equals(...) == true`); favourites files are user-editable.

### M4. Recordings go to `%USERPROFILE%\Music`, not the user's real Music folder — done in 1.1.0
`Services/Storage.cs:39`.
When Music is redirected to OneDrive or another drive, which is common on Windows 11, recordings land in a
`Music` folder that Windows does not show as the Music library. **Fix:** use
`Environment.SpecialFolder.MyMusic`, falling back to `UserProfile\Music`. Also replace the hard-coded
"in Music\\MightyRadio" text at `MainWindow.xaml.cs:220` with the actual folder.

### M5. Radio Browser usage does not follow the API's guidance — done in 1.1.0
`Services/RadioBrowserClient.cs:14-18, 26`.
- **Server discovery:** the servers are hard-coded (`de1`, `de2`). Radio Browser asks clients to discover mirrors through DNS (`_api._tcp.radio-browser.info` SRV records, or the A records of `all.api.radio-browser.info`) or via `https://all.api.radio-browser.info/json/servers`, then pick one at random. When checked on 3 Nov 2026, `/json/servers` listed only `de1`. **Fix:** on startup, fetch `/json/servers`, shuffle it, and cache it in settings with a timestamp. Keep `de1`/`de2` only as a last-resort fallback.
- **User-Agent:** the API asks for a descriptive one. `MightyRadio/1.0` has no contact or URL. **Fix:** use `MightyRadio/<assembly version> (+https://github.com/florence-experiments/MightyRadio)` and take the version from the assembly so it can't go stale.
- **Compression:** use `new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })`. JSON responses for 300 stations compress about 10x, and the connection lifetime picks up DNS changes.
- **Click reporting:** `ReportClick` (`:91-104`) fires on every Play press, including retries of the same station. Report at most once per station per session.

### M6. A transient favourites read error can wipe favourites on exit — done in 1.1.0
`Services/Storage.cs:44-58` and `MainWindow.xaml.cs:534`.
Any exception while loading, such as a file briefly locked by antivirus or a OneDrive sync, returns an empty
list. `OnClosing` then unconditionally saves favourites, overwriting the real file. The `.bad` copy helps only
if the copy itself succeeds, and it is overwritten on the next failure.
**Fix:** distinguish "file missing" from "file unreadable". After a read failure, set a flag that blocks saving
until the user changes favourites. Write `.bad` with a timestamp suffix. Save favourites only when they changed
(they are already saved on every edit).

### M7. Dropped streams are not reconnected — done in 1.1.0
`Services/RadioPlayer.cs:48, 188-214`.
On Wi-Fi blips, live streams raise `EndReached`, and the user gets "The station stopped sending audio".
`--http-reconnect` only covers some HTTP cases. **Fix:** when `EndReached` or `EncounteredError` arrives
after the stream was `Playing`, retry the same URL up to three times with back-off (2 s, 5 s, 10 s) while
showing "Reconnecting…". Show the error only after that.

### M8. Keyboard focus is invisible in lists and tabs — done in 1.1.0
`Themes/Dark.xaml:203` (`ListBoxItem`) and `:246` (`TabItem`).
Both set `FocusVisualStyle={x:Null}` and have no `IsKeyboardFocused` trigger. Keyboard users can't see where
they are, although buttons do have a focus trigger (`:45`). The custom `ComboBox` template (`:147-182`) has no
focus indication either. **Fix:** add a 2 px accent border on `IsKeyboardFocusWithin` / `IsKeyboardFocused`
in each template.

### M9. Screen readers miss status messages and icon-only buttons — done in 1.1.0
`MainWindow.xaml:44` (`MessageText`), `:38` (`StatusLabel`), `:221-226` (▲/▼ buttons).
- Error and status messages are not announced. **Fix:** set `AutomationProperties.LiveSetting="Polite"` on `MessageText` and `StatusLabel`, and in `ShowMessage` call `UIElementAutomationPeer.FromElement(MessageText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)`.
- The move buttons are read as "black up-pointing triangle". **Fix:** add `AutomationProperties.Name="Move up"` / `"Move down"`, and do the same for the Record button, whose text changes.

### M10. Scroll bars are the default narrow ones — done in 1.1.0
`Themes/Dark.xaml` has no `ScrollBar` style, so lists use the stock 17 px Aero scroll bar, which also looks
light-grey on the dark theme. Chris often mis-taps narrow scroll bars and has asked for about 3x the width.
**Fix:** add an implicit `ScrollBar` style. Set `Width` (vertical) and `Height` (horizontal) to 48, use a
rounded `Thumb` with `MinHeight=60` and `PanelAltBrush`/`AccentBrush` colours, and give the `RepeatButton`s
large hit areas. Also set `ScrollViewer.PanningMode="VerticalOnly"` on the `ListBox` style for touch. Put the
width in a resource (`ScrollBarSize`) so it can become a setting.

### M11. Duplicate scanner URLs share one favourites key — done in 1.1.0
`stations-scanner.json` (Broadcastify "top" URL is used three times: Air / ATC, Public safety,
Amateur / ham) and `Models/ScannerFeed.cs:20` (`StationUuid = "scanner:" + Url`).
Favouriting one of these entries marks all three as favourites. **Fix:** key on
`"scanner:" + category + "|" + url`, or dedupe the list. Use the specific Broadcastify genre pages
(for example `/listen/top?genre=aviation`) instead of three identical links.

---

## Low priority

| # | Where | Issue | Fix |
|---|---|---|---|
| L1 ✅ | `App.xaml.cs:21-28` | The second instance shows a message box instead of bringing the running window forward. | Use a named `EventWaitHandle`. The first instance waits on it and calls `Activate()`/restores the window. |
| L2 ✅ | `App.xaml.cs:55-63` | A repeating UI exception (for example in a binding) shows an endless series of "Oops" boxes. | Show the dialog at most once per 30 s. Log the rest. |
| L3 ✅ | `MainWindow.xaml.cs:277-279` | Old `CancellationTokenSource`s from cancelled searches are never disposed. | `var old = Interlocked.Exchange(ref _searchCts, cts); old?.Cancel(); old?.Dispose();` |
| L4 ✅ | `MainWindow.xaml.cs:260` | The country lookup uses `CancellationToken.None`, so closing during start-up runs it against a disposed `HttpClient` (caught, but logged as an error). | Use a window-lifetime CTS that is cancelled in `OnClosing`. |
| L5 | `MainWindow.xaml.cs:295` | 300 `ObservableCollection.Add` calls each raise `CollectionChanged`. | Assign a new collection or `ItemsSource` in one go. |
| L6 | `MainWindow.xaml.cs:47-48, 528-532` | Size is saved but not position, and a maximised window is not remembered. | Save `RestoreBounds` and `WindowState`, and clamp them to `SystemParameters.VirtualScreen*` on load. |
| L7 ✅ | `Services/Recorder.cs:63` | `StopAsync().Wait()` runs on the UI thread. It is not deadlock-prone (it uses `ConfigureAwait(false)`), but it freezes the UI for up to 3 s. | Make `Start` async, or have callers await `StopAsync` first. |
| L8 ✅ | `Services/Recorder.cs:72-88` | A recording that never connects shows a ticking timer and silently produces 0 bytes. | After 15 s, check `FileInfo(path).Length`. If it is still 0, raise `Failed`. |
| L9 ✅ | `Services/RadioPlayer.cs:43` | VLC sends its default `VLC/3.0.x LibVLC/3.0.x` user agent. A few CDNs block it. | Call `LibVlc.SetUserAgent("Mighty Radio", "MightyRadio/1.0")`. |
| L10 | `Services/ScannerRepository.cs:16-27` | After first run, the user's copy is never updated, so new or fixed bundled feeds never reach existing users. | Add `"version"` to the JSON. When the bundled file is newer, offer to merge new entries by URL without touching the user's edits. |
| L11 ✅ | `Services/Storage.cs:93` | When the log reaches 1 MB, the whole log is deleted, including the most recent context. | Rotate to `error.old.log` instead of deleting. |
| L12 ✅ | `Services/Storage.cs` (log calls in `RadioPlayer.cs:98`) | `error.log` stores full stream URLs, some of which contain listener tokens. It never leaves the PC, but users may paste it into public bug reports. | Strip query strings from logged URLs. |
| L13 ✅ | `MainWindow.xaml.cs:386` | "Edit list" hard-codes `notepad.exe`. | Open the `.json` with `UseShellExecute` and the `edit` verb, falling back to Notepad. |
| L14 ✅ | `MightyRadio.csproj:21` | The version is hard-coded at 1.0.0, and the user agent repeats it separately. | Set it once via `<Version>` and have CI stamp it (see ideas). |

---

## Scanner feed list – notes (neutral)

- **No UK police, fire or ambulance feeds are included.** The "Public safety" category contains US, Canadian,
  Australian and Taiwanese feeds:
  - streams: SomaFM SF Police Scanner and SF 10-33, Kamloops Fire Rescue, Perth Fire & Rescue (WARSUG), Travis County Police, ZT Dispatch, Taiwan Police Broadcasting Service
  - browser links to Broadcastify pages for Buffalo, Cleveland, Detroit, Indianapolis, Pittsburgh and Sacramento.

  The Taiwan PBS feed is a licensed public broadcaster rather than intercepted traffic.
- **UK legal context.** Section 48 of the Wireless Telegraphy Act 2006 makes it an offence to use radio
  apparatus to obtain information from a transmission you are not authorised to receive, or to disclose such
  information. Ofcom's long-standing guidance is that only broadcast, amateur and CB transmissions are
  intended for general reception. Several entries are non-broadcast transmissions: ATC and oceanic control
  (LiveATC links, Shanwick 8879 kHz), marine VHF ch 16 (Nantucket, USCG Long Island Sound, IJmuiden port/SAR)
  and the public-safety feeds. Mighty Radio receives none of these over the air. It plays or links to
  third-party internet streams, which are generally regarded as a grey area rather than clearly covered by the
  Act. Recording such a feed and sharing it is closer to "disclosure" than just listening. Broadcast-type
  entries are intended for the public: NOAA weather, VOLMET, NAVTEX, WWV, shortwave broadcasters, SomaFM and
  the morse practice streams. Suggested neutral mitigations:
  - a one-line note in the README and at the top of the Scanner tab ("Some feeds relay non-broadcast radio traffic; check what is permitted where you live");
  - optionally, disabling Record for the "Public safety", "Air / ATC" and "Marine / VHF" categories.
- **Third-party terms.** LiveATC and Broadcastify do not permit their streams to be embedded in other apps
  without permission. The list correctly uses `"type": "web"` for both, so they open on the providers' own
  sites. Keep it that way. The `research/` scratch folder (excluded from this repo) contains direct
  `d.liveatc.net` stream URLs and should not be published.
- **Entries worth re-checking:**
  - "ZT Dispatch 7500 Repeater" (`http://philipiptv.cloudns.ph:8082/…`): unknown operator on a dynamic-DNS host, plain HTTP.
  - "Radio Rebelde" is played through a third-party proxy (`securestreams7.autopo.st/?uri=…`) rather than the broadcaster's own URL.
  - "Travis County Police" is labelled "Legacy RadioReference URL" in its own notes.

  An automated probe from the build box was inconclusive: plain-HTTP and some port-8443 hosts failed from that
  network, and LiveATC returns 403 to scripts. The feed health checker below would answer this properly from
  the user's PC.

---

## Feature ideas (grounded in the current code)

1. **Steampunk / sci-fi theme ("Brass & Phosphor").** All colours live in `Themes/Dark.xaml:5-19` and every
   control is already templated, so a second `ResourceDictionary` (`Themes/Brass.xaml`) can be swapped in
   `App.xaml` with no code changes:
   - deep gunmetal background `#121417` with brushed-metal panel gradients;
   - brass accents `#B5893B` / `#D9A441`, rivet-dot corners on panels;
   - a green-phosphor or amber VFD readout for `StatusLabel` and `NowPlayingText` (a monospace font such as Cascadia Mono, a soft glow `DropShadowEffect`);
   - a needle-style VU or tuning meter (see 6);
   - square-ish 4 px corners instead of the current 8–12 px rounded "friendly" look.

   It should stay restrained (no cartoon gears or skeuomorphic wood). Add a Theme picker in settings
   (`AppSettings.Theme`) and switch with `Application.Current.Resources.MergedDictionaries`.
2. **Wide scroll bars as a setting.** Build on M10: add `AppSettings.ScrollBarSize` (default 48, choices
   17/32/48/64) bound to a `DynamicResource`.
3. **Now-playing history.** `RadioPlayer.NowPlayingChanged` already surfaces ICY `StreamTitle`
   (`RadioPlayer.cs:216-236`). Keep the last 50 titles with timestamps and station in a "Heard" panel, with
   copy and "search the web" buttons, and optionally save them into the recording as a `.cue` file.
4. **Sleep timer.** A `DispatcherTimer` with 15/30/60/90 min presets that fades `_player.Volume` to 0 over the
   last 60 s, then calls `StopButton_Click`. Show the remaining time next to `StatusLabel`.
5. **Scheduled recording.** "Record station X from 19:00 for 60 min" stored in `schedule.json`. While the app
   runs, use a `DispatcherTimer`. For unattended runs, register a Windows Task Scheduler task that launches
   `MightyRadio.exe --record "<station key>" --minutes 60 --minimised`. This needs a small command-line path in
   `App.OnStartup` that bypasses the single-instance message.
6. **Equaliser and level meter.** LibVLC 3 exposes `Equalizer` presets (`new Equalizer(presetIndex)`,
   `_mp.SetEqualizer`). Add a preset drop-down (Flat, Voice/Speech, which suits scanner feeds, Classical, Rock)
   and optional 10-band sliders, remembered per station key. For a meter, use `_mp.SetAudioCallbacks` (or a
   cheaper fake from `Media.Statistics`) to drive the VU needle in the sci-fi theme.
7. **Mini player and tray.** A compact always-on-top window (station, now playing, play/stop, volume) and a
   `NotifyIcon`-style tray icon. WPF has none built in; use the WinForms `NotifyIcon` via
   `<UseWindowsForms>true</UseWindowsForms>`, or the `H.NotifyIcon.Wpf` package. Close-to-tray should be an
   option, not the default.
8. **Global media keys and a Windows media overlay.** Register Play/Pause/Stop via
   `RegisterHotKey(VK_MEDIA_PLAY_PAUSE…)`, or better, integrate with
   `Windows.Media.SystemMediaTransportControls` (needs `net8.0-windows10.0.19041`), so keyboard media keys,
   Bluetooth headsets and the Windows volume flyout show the station and ICY title. Also add in-app shortcuts:
   Space play/stop, Ctrl+R record, Ctrl+F search, Ctrl+D favourite, +/- volume, M mute (no mute exists today).
9. **Feed health checker.** A "Check feeds" button on the Scanner tab. It runs bounded-parallel `HEAD`/ranged
   `GET`s against `type: "stream"` URLs (same `HttpClient`, 8 s timeout, `Icy-MetaData: 1`), marks each row
   ✓ / ✗ / ? with the content type and ICY name, and offers "hide dead feeds". It runs only when the user asks,
   which is consistent with the no-telemetry promise.
10. **Import / export.** Export favourites and scanner list as `.json` and standard `.m3u8`/`.pls`. Import
    `.m3u`/`.pls`/`.json` by drag-and-drop onto the window. This also makes moving to a new PC easy.
11. **Auto-reconnect and buffering setting.** See M7. Expose `--network-caching` (currently fixed at 2000 ms)
    as Normal / Patchy Wi-Fi (5000 ms).
12. **Auto-update check (opt-in).** On request, or weekly if enabled, call the GitHub Releases API
    (`/repos/florence-experiments/MightyRadio/releases/latest`), compare with the assembly version, and open the release
    page. Do not self-replace files. Keep it off by default to honour the "no telemetry" line in the README,
    and say exactly what is contacted.
13. **Code signing.** The README tells users to click past SmartScreen. Options include Azure Trusted Signing,
    which is cheap and works for individuals in many regions, or a standard OV certificate. Sign
    `MightyRadio.exe` in CI with `signtool sign /fd sha256 /tr <timestamp> /td sha256`. Also publish SHA-256
    checksums of the zip and 7z with each release.
14. **CI builds.** A GitHub Actions workflow on `windows-latest`:
    - `dotnet publish MightyRadio/MightyRadio.csproj -c Release -r win-x64 --self-contained -o out`;
    - zip `out` plus `README.txt`, `LICENSE` and `THIRD-PARTY-NOTICES.md`;
    - attach it to a tag-triggered release.

    Add a small `MightyRadio.Tests` project for the pure functions (`Recorder.BuildSout`,
    `Recorder.SafeFileName`, `ScannerRepository` parsing with nulls and comments, `Station.Key`). These are
    already public and static for that reason (`Recorder.cs:40`).
15. **Smaller download.** The zip is about 114 MB, mostly LibVLC plugins for video. Trim
    `VlcWindowsX64IncludeFiles` (`MightyRadio.csproj:37`) to the audio, access, demux, codec and stream_out
    plugins needed for radio (for example drop `plugins/video_*`, `plugins/visualization`, `plugins/gui`, `lua`
    and `hrtfs` if unused). Test against the scanner list plus a few AAC/HLS stations. This should roughly halve
    the size.
16. **"Pause" for live radio (time-shift).** LibVLC supports `--input-timeshift-granularity` / the
    `timeshift` module. A Pause button that buffers to `%TEMP%` lets the user resume where they left off.
