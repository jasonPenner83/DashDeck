# Third-party notices

DashDeck's own source is [MIT](LICENSE). It depends on the packages and services below,
which keep their own licenses. This list covers what the solution references; check it
again whenever a `PackageReference` is added.

## Shipped with the app

| Package | Used by | License |
|---|---|---|
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | Host (MVVM, ADR-0011) | MIT |
| [LibVLCSharp.WPF](https://code.videolan.org/videolan/LibVLCSharp) | Host (video stage) | LGPL-2.1-or-later |
| [VideoLAN.LibVLC.Windows](https://code.videolan.org/videolan/libvlc-nuget) | Host (native VLC) | LGPL-2.1-or-later (libVLC core); individual VLC plugins carry their own licenses, some GPL |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | Host (web stage) | Microsoft WebView2 SDK license (BSD-style) |
| [System.IO.Ports](https://github.com/dotnet/runtime) | Vehicle transports | MIT |
| .NET runtime (self-contained publish) | Everything | MIT |

## Test-only

| Package | License |
|---|---|
| xunit, xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |
| coverlet.collector | MIT |

## Services called at runtime

| Service | What for | Terms |
|---|---|---|
| [Open-Meteo](https://open-meteo.com/) | Weather on the clock face | Free for non-commercial use; data is **CC BY 4.0** and must be attributed |
| OpenStreetMap, Spotify, Apple Music web apps | Loaded as-is in the web stage | Each site's own terms; DashDeck only hosts the page |

## Before publishing a binary release

Source-only publication needs nothing more than this file. A **GitHub Release with the
self-contained zip** redistributes libVLC and its plugins, so that zip must include the
LGPL/GPL license texts from the VLC package, keep libVLC as separately replaceable DLLs
(it is — nothing is statically linked), and show Open-Meteo's attribution in the app.
