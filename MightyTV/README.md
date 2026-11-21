# Mighty TV

A small sister app to [Mighty Radio](../README.md) for worldwide live television. This is a lab slice:
it loads the whole public [iptv-org](https://github.com/iptv-org/api) directory (every country and
category, including streams that are likely to be dead or geo-blocked) and plays one channel at a time.

There is no recording, favourites list, or dead-link scan.

## Layout

| Project | What |
|---|---|
| `Mighty.Directory/` | Plain **net8.0** class library. Channel models, iptv-org download, join, and name/country/category filtering. No WPF (or MAUI) references, so a later Android app can use it. |
| `MightyTV/` | .NET 8 WPF player for Windows x64. Dark theme and control styles are copied from Mighty Radio. LibVLC plays the selected stream in a `VideoView`. |

Mighty Radio is unchanged. The two apps are not merged.

## Using it

1. On startup the app downloads `channels.json`, `streams.json`, `countries.json` and `categories.json` from `https://iptv-org.github.io/api/`.
2. The list defaults to the **United Kingdom**. Pick **Any country** or another country, and optionally a category.
3. Type part of a name and press **Search** or Enter. Matching includes alternative names from the directory.
4. Double-click a row, or select it and press **Play**. **Stop** and the volume slider do what they say.
5. A stream that fails, ends immediately, or does not answer within 20 seconds shows a status message and does not close the app.

Only `http` and `https` addresses are played. When a channel has several streams, the highest resolution http(s) URL is used. `user_agent` and an http(s) `referrer` from the directory are passed through to VLC. Streams with no channel id still appear when the country filter is **Any country**.

Many directory entries do not actually play (taken down, geo-blocked, or tokenised). That is expected for this slice.

## Build

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). The WPF project sets `EnableWindowsTargeting` and `RuntimeIdentifier=win-x64`, so it compiles on Linux and macOS as well as Windows. It only *runs* on Windows.

```powershell
dotnet test Mighty.Directory.Tests/Mighty.Directory.Tests.csproj
dotnet build MightyTV/MightyTV.csproj -c Release

# Optional: parse the live iptv-org files (needs network)
$env:MIGHTY_IPTV_LIVE = "1"   # bash: MIGHTY_IPTV_LIVE=1
dotnet test Mighty.Directory.Tests/Mighty.Directory.Tests.csproj --filter Live_iptv_org
```

The VLC engine comes from `VideoLAN.LibVLC.Windows` and is copied to `libvlc\win-x64` next to the app. Unlike Mighty Radio, the full engine is included, because television needs the video plugins.

```powershell
dotnet publish MightyTV/MightyTV.csproj -c Release -r win-x64 --self-contained -o publish/MightyTV
```

Run `MightyTV.exe` from that folder. Keep `libvlc` beside it.

Before changing XAML, `python3 tools/check-xaml-resources.py MightyTV` catches bad resource keys. The Mighty TV build runs it automatically on Linux and macOS.

## Privacy

Mighty TV contacts iptv-org's GitHub Pages to download the directory, then the stream you press Play on. The directory request uses `MightyTV/0.1 (+https://github.com/florence-experiments/MightyRadio)`. There is no account and no telemetry.

Streams belong to their broadcasters. This lab build does not filter the directory and does not grant any right to watch a channel.

## Checked and not checked

The directory library has fixture tests, and a live run of `MIGHTY_IPTV_LIVE=1` parsed the public JSON (thousands of playable rows, United Kingdom present). `dotnet build MightyTV/MightyTV.csproj -c Release` produces a win-x64 `MightyTV.exe` with the full `libvlc` folder.

The WPF window was not opened. Playback, the video surface, volume, stop, and the dead-stream message still need a Windows machine.
