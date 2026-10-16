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
| [MapLibre GL JS](https://github.com/maplibre/maplibre-gl-js) 6.12.0 — `src/DashDeck.Host/Map/web/lib/`, deployed as `map/lib/` | The map renderer (ADR-0063) | BSD-3-Clause — text in `map/lib/LICENSE-maplibre-gl.txt`, shipped beside it |

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

## Services called at runtime

| Service | What for | Terms |
|---|---|---|
| [Open-Meteo](https://open-meteo.com/) | Weather on the clock face | Free for non-commercial use; data is **CC BY 4.0** and must be attributed |
| OpenStreetMap, Spotify, Apple Music web apps | Loaded as-is in the web stage | Each site's own terms; DashDeck only hosts the page |
| [OpenFreeMap](https://openfreemap.org) | Map tiles and style, fetched as you drive (ADR-0063) | No key and no stated request limit; commercial use allowed. Donation-funded with no SLA — the offline half exists partly for that reason. **Attribution required** |
| [OpenMapTiles](https://openmaptiles.org) schema | The tiles OpenFreeMap serves are built to it | Schema **CC-BY**; must be credited |
| [Nominatim](https://nominatim.openstreetmap.org) | Turning a typed address into a position (ADR-0065) | OSM Foundation **acceptable-use policy**: end-user-triggered searches only, **no auto-complete**, at most one request a second, caching expected, and a User-Agent that identifies the application. All five are enforced in code (`AddressQuery`, `Geocoder`, `MapSearchViewModel`) rather than left to good intentions |
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
