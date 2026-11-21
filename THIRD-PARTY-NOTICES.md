# Third-party notices

Mighty Radio's own source code is MIT-licensed (see `LICENSE`). The MIT licence covers only the code in this
repository. The binary releases also bundle the components below, which keep their own licences.

| Component | Version | Licence | Source |
|---|---|---|---|
| LibVLC (VideoLAN.LibVLC.Windows NuGet package: `libvlc.dll`, `libvlccore.dll` and an audio-only subset of `plugins/`) | 3.0.24 | LGPL-2.1-or-later (package licence expression) | https://code.videolan.org/videolan/vlc (3.0.x branch), https://code.videolan.org/videolan/libvlc-nuget |
| LibVLCSharp | 3.10.2 | LGPL-2.1-or-later (package licence expression) | https://code.videolan.org/videolan/LibVLCSharp |
| .NET 8 runtime and Windows Desktop runtime (self-contained publish) | 8.0.x | MIT (plus the runtime's own third-party notices) | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |
| Windows SDK projection for .NET (`Microsoft.Windows.SDK.NET.dll`, `WinRT.Runtime.dll`, from the Microsoft.Windows.SDK.NET.Ref package, used for the Windows media overlay) | 10.0.19041.56 | Microsoft Windows SDK licence terms (https://aka.ms/WinSDKLicenseURL); C#/WinRT runtime is MIT | https://github.com/microsoft/CsWinRT |

The licence expressions above come from the `<license>` element of each package's `.nuspec` in the local
NuGet cache (`libvlcsharp/3.10.2`, `videolan.libvlc.windows/3.0.24`).

## LGPL obligations when you redistribute the binaries

- LibVLC and LibVLCSharp are loaded dynamically as separate DLLs (`PublishSingleFile` is `false`). Users can
  therefore replace them with their own builds, as the LGPL requires. Keep this arrangement. Merging them into
  a single-file or trimmed bundle needs a fresh licence review.
- Ship the LGPL-2.1 text with each binary release. It is in `licenses/LGPL-2.1.txt` (from
  https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt) and the build copies it, this file and `LICENSE`
  into the release folder automatically. The exact source versions are listed above.
- Some individual VLC plugins and the third-party libraries compiled into them have their own licences, and a
  few modules are GPL-2.0-or-later. Upstream builds list these in VLC's `COPYING` and `NEWS`/contrib notices.
  Since v1.1 the release ships only the audio plugins listed in `MightyRadio.csproj` (HTTP/HTTPS access,
  GnuTLS, XML, playlist/HLS/DASH and audio demuxers, `avcodec`, `mpg123`, Vorbis, Opus, FLAC, Speex and raw
  decoders, audio packetizers/filters/mixers/outputs, and the stream-dump, TS/Ogg mux and file output modules
  used for recording). Video, subtitle, disc, visualisation, Lua and streaming-server modules are not shipped.
  The licence of each remaining plugin has not been audited individually; if any GPL-only module is among them,
  the distributed bundle as a whole should be treated as GPL-compatible. Mighty Radio's MIT code is compatible
  with that.

## Data and services

- Station directory data: Radio Browser (https://www.radio-browser.info). It is a free, community-maintained
  directory; please respect its API usage guidance. Mighty Radio's use of it is not endorsed by the project.
- Scanner feeds in `stations-scanner.json` are links to streams and web pages run by third parties (for example
  SomaFM, wxradio.org, LiveATC.net, Broadcastify, the University of Twente WebSDR). Those operators' terms
  apply. Mighty Radio does not host or redistribute any of their audio.

## Mighty TV

`MightyTV/` is a separate lab app in this repository. It uses the same LibVLC 3.0.24 and LibVLCSharp 3.10.2 packages as Mighty Radio, but it ships the full Windows x64 plugin set, including video decoders and outputs, rather than the audio-only subset. The LGPL notes above apply to that wider set. Channel data comes from the public iptv-org API (https://github.com/iptv-org/api). Mighty TV downloads that JSON when it starts and does not redistribute the streams.

## Icon

`MightyRadio/Assets/MightyRadio.ico`: its origin is not recorded in the source. Before publishing, confirm it
was made for this project or that its licence allows redistribution.
