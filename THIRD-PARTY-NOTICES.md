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
| [System.Management](https://github.com/dotnet/runtime) | Host (the tablet's backlight, ADR-0068) | MIT |
| .NET runtime (self-contained publish) | Everything | MIT |
| [MapLibre GL JS](https://github.com/maplibre/maplibre-gl-js) 6.12.0 — `src/DashDeck.Host/Map/web/lib/`, deployed as `map/lib/` | The map renderer (ADR-0063) | BSD-3-Clause — text in `map/lib/LICENSE-maplibre-gl.txt`, shipped beside it |

### Android on the dash (ADR-0075)

In the dash itself (`DashDeck.Emulator`, behind the ANDROID cell) and in `AndroidProbe\` beside it.

| Package or file | Used by | License |
|---|---|---|
| [Grpc.Net.Client](https://github.com/grpc/grpc-dotnet) 2.84.0 | DashDeck.Emulator and AndroidProbe (gRPC to the Android Emulator) | Apache-2.0 |
| [Google.Protobuf](https://github.com/protocolbuffers/protobuf) 3.36.2 | DashDeck.Emulator and AndroidProbe (gRPC messages) | BSD-3-Clause |
| [Grpc.Tools](https://github.com/grpc/grpc) 2.84.0 | Build only (generates the client); not shipped | Apache-2.0 |
| `emulator_controller.proto` — `src/DashDeck.Emulator/Protos/` and `src/DashDeck.AndroidProbe/Protos/`, vendored unchanged from [google/android-emulator-webrtc](https://github.com/google/android-emulator-webrtc) | The emulator's gRPC service definition | Apache-2.0 (The Android Open Source Project), header kept in the file |

The Android SDK, the emulator and the system images are **not** shipped or downloaded by DashDeck:
the person installs them with Android Studio, under Google's
[Android SDK License Agreement](https://developer.android.com/studio/terms), whose terms for using
the images to run Android rather than to develop apps are theirs to read (ADR-0075).

ANDROID PHONE (ADR-0076) uses the person's own install of
[scrcpy](https://github.com/Genymobile/scrcpy) (Apache-2.0, Genymobile and Romain Vimont): DashDeck
pushes that install's `scrcpy-server` to the phone and speaks its protocol, as scrcpy's own client
does. **No part of scrcpy is shipped or downloaded by DashDeck.** The H.264 it sends is decoded by the
LibVLC already listed above.

### Fonts

| Font | Used by | License |
|---|---|---|
| [Antonio](https://github.com/googlefonts/antonioFont) (Regular, Bold) — `catalog/themes/` | The *LCARS (inspired)* theme (ADR-0036) | SIL Open Font License 1.1 — text in `catalog/themes/OFL-Antonio.txt`, shipped beside the font |

## Test-only

| Package | License |
|---|---|
| xunit, xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |
| coverlet.collector | MIT |
| Grpc.AspNetCore.Server (a fake Android Emulator for the gRPC client's tests, ADR-0075) | Apache-2.0 |

## Services called at runtime

| Service | What for | Terms |
|---|---|---|
| [Open-Meteo](https://open-meteo.com/) | Weather on the clock face | Free for non-commercial use; data is **CC BY 4.0** and must be attributed |
| OpenStreetMap, Spotify, Apple Music web apps | Loaded as-is in the web stage | Each site's own terms; DashDeck only hosts the page |
| [OpenFreeMap](https://openfreemap.org) | Map tiles and style, fetched as you drive (ADR-0063) | No key and no stated request limit; commercial use allowed. Donation-funded with no SLA — the offline half exists partly for that reason. **Attribution required** |
| [OpenMapTiles](https://openmaptiles.org) schema | The tiles OpenFreeMap serves are built to it | Schema **CC-BY**; must be credited |
| [OSRM at FOSSGIS](https://routing.openstreetmap.de/about.html) | Working out a route to a searched place (ADR-0065's addendum) | FOSSGIS **usage policy**: attribution on screen, an identifying User-Agent, at most one request a second, no scraping or heavy use. Enforced in code (`Router`, `MapRouteViewModel`), and the reason live turn-by-turn waits for the bundled engine — following a vehicle would be heavy use |
| [Nominatim](https://nominatim.openstreetmap.org) | Turning a typed address into a position (ADR-0065) | OSM Foundation **acceptable-use policy**: end-user-triggered searches only, **no auto-complete**, at most one request a second, caching expected, and a User-Agent that identifies the application. All five are enforced in code (`AddressQuery`, `Geocoder`, `MapSearchViewModel`) rather than left to good intentions |
| [Sentinel-2 cloudless](https://s2maps.eu) by EOX IT Services | The satellite map (ADR-0071) | The 2020 mosaic: **non-commercial use, attribution required** — "Sentinel-2 cloudless by EOX IT Services GmbH (contains modified Copernicus Sentinel data 2020)", carried in the style's own attribution |
| [Terrain Tiles on AWS](https://registry.opendata.aws/terrain-tiles/) | Hill-shading on the terrain map (ADR-0071) | An AWS open dataset, free to fetch, no key. Built by Mapzen from SRTM, GMTED, USGS 3DEP, Natural Resources Canada's CDEM and others; **attribution required**, carried in the style |
| OpenStreetMap data (via OpenFreeMap) | Everything on the map | **ODbL** — © OpenStreetMap contributors, and the credit is a licence condition, not a courtesy |

**The map's attribution line** is `© OpenStreetMap contributors · OpenMapTiles · OpenFreeMap`
(`MapSource.Attribution`). MapLibre draws the style's own attribution control on the map, and the
dash repeats it in the loading state — both are deliberate, because the obligation survives a map
that has not finished drawing.

**Not used, deliberately:** OpenStreetMap's own tile servers. Their
[tile usage policy](https://operations.osmfoundation.org/policies/vector/) prohibits fetching
tiles ahead of being viewed and names archives and offline use — the exact shape of a dash that
keeps what it downloads. The data is the same; the capacity to serve it is what the policy
protects.

## Before publishing a binary release

Source-only publication needs nothing more than this file. A **GitHub Release with the
self-contained zip** redistributes libVLC and its plugins, so that zip must include the
LGPL/GPL license texts from the VLC package, keep libVLC as separately replaceable DLLs
(it is — nothing is statically linked), and show Open-Meteo's attribution in the app.
