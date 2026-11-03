# Third-party notices

Mighty Radio's own source code is MIT-licensed (see `LICENSE`). The MIT licence covers only the code in this
repository. The binary releases also bundle the components below, which keep their own licences.

| Component | Version | Licence | Source |
|---|---|---|---|
| LibVLC (VideoLAN.LibVLC.Windows NuGet package: `libvlc.dll`, `libvlccore.dll`, `plugins/`, `lua/`, `hrtfs/`) | 3.0.24 | LGPL-2.1-or-later (package licence expression) | https://code.videolan.org/videolan/vlc (3.0.x branch), https://code.videolan.org/videolan/libvlc-nuget |
| LibVLCSharp | 3.10.2 | LGPL-2.1-or-later (package licence expression) | https://code.videolan.org/videolan/LibVLCSharp |
| .NET 8 runtime and Windows Desktop runtime (self-contained publish) | 8.0.x | MIT (plus the runtime's own third-party notices) | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |

The licence expressions above come from the `<license>` element of each package's `.nuspec` in the local
NuGet cache (`libvlcsharp/3.10.2`, `videolan.libvlc.windows/3.0.24`).

## LGPL obligations when you redistribute the binaries

- LibVLC and LibVLCSharp are loaded dynamically as separate DLLs (`PublishSingleFile` is `false`). Users can
  therefore replace them with their own builds, as the LGPL requires. Keep this arrangement. Merging them into
  a single-file or trimmed bundle needs a fresh licence review.
- Ship the LGPL-2.1 text with each binary release. The full text is at
  https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt; `COPYING.LIB` in the VLC source works too. Also
  ship a pointer to the exact source versions listed above.
- Some individual VLC plugins and the third-party libraries compiled into them have their own licences, and a
  few modules are GPL-2.0-or-later. Upstream builds list these in VLC's `COPYING` and `NEWS`/contrib notices.
  If you prune the `plugins/` folder (see `docs/IMPROVEMENTS.md`, idea 15), keep only what you need. If any
  GPL-only module stays in the bundle, the distributed bundle as a whole should be treated as GPL-compatible.
  Mighty Radio's MIT code is compatible with that.

## Data and services

- Station directory data: Radio Browser (https://www.radio-browser.info). It is a free, community-maintained
  directory; please respect its API usage guidance. Mighty Radio's use of it is not endorsed by the project.
- Scanner feeds in `stations-scanner.json` are links to streams and web pages run by third parties (for example
  SomaFM, wxradio.org, LiveATC.net, Broadcastify, the University of Twente WebSDR). Those operators' terms
  apply. Mighty Radio does not host or redistribute any of their audio.

## Icon

`MightyRadio/Assets/MightyRadio.ico`: its origin is not recorded in the source. Before publishing, confirm it
was made for this project or that its licence allows redistribution.
