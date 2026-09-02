# DashDeck — Open Questions

Live list. Resolved items are struck through and keep their reasoning; the ones that
settle architecture move to an ADR in [`decisions/`](decisions/).

## Blocking nothing right now (mock-data phase)

| # | Question | Notes |
|---|----------|-------|
| ~~Q1~~ | ~~Bluetooth MX+ or wired EX?~~ | **Resolved 2026-09-01 → ADR-0007.** Wired USB, OBDLink EX. Electronic bus switching was the deciding property. |
| ~~Q2~~ | ~~Is the dock scenario real enough to build `UsbSerialTransport` early?~~ | **Resolved by Q1.** `UsbSerialTransport` is the primary transport and gets built first; Bluetooth may never be built at all. *Amended by Q16:* this originally said the tablet had no USB-A so a dock or adapter was on the critical path. That was written against a Pro 9. The Pro 7 has USB-A, so nothing is on the critical path. |
| Q3 | Mount hardware and power in the cab. | Affects nothing in software, but decides portrait geometry and whether the tablet charges while docked. |
| Q4 | Does this truck support the fuel-rate PID (0x5E)? | Unanswerable until an adapter exists. MAF fallback plus tank calibration is designed to cover either answer (risk R5). |
| Q5 | How much does the Gateway Module filter at the OBD-II port? | Measured on first bring-up. Determines whether MS-CAN signals are reachable without a behind-dash tap (risk R4). |
| ~~Q11~~ | ~~Dock, or a bare USB-C adapter?~~ | **Resolved 2026-09-01 by Q16.** Neither. The Pro 7 has a **USB-A 3.0 port**, so the EX plugs straight in and power goes over Surface Connect. The dock leaves the bring-up plan entirely. |
| Q12 | What is the *real* sustained request ceiling over USB? | **Machinery built, answer still unknown.** `VehicleService.MeasuredRequestsPerSecond` measures it continuously from request service time and feeds the arbiter's budget, so the number will be read off the truck rather than estimated. Until then the simulator holds the pessimistic 15 req/sec figure. It decides whether live gauges are viable. |
| ~~Q16~~ | ~~Which Surface is the truck tablet?~~ | **Resolved 2026-09-01. A Surface Pro 7 (Intel)**, not the Pro 9 the docs recorded. 12.3", 2736 × 1824 at 200%, so **912 × 1368 portrait**. Two consequences: the band arithmetic is re-derived under B2, and the EX no longer needs a dock (Q11). ADR-0001 and ADR-0005 still say "Pro 9" in their context sections — left alone deliberately, since ADRs record what was believed when the decision was made and neither decision turns on the model. |

## Product questions for you

| # | Question | Notes |
|---|----------|-------|
| Q6 | After the trip computer, which component do you actually want next? | The outline assumes gauges as the SDK-proving second component, but that is a placeholder, not a decision. |
| Q7 | Should trip history stay strictly local, or sync somewhere? | Local-only is the default and the simplest. Worth deciding before the storage schema sets. |
| Q8 | Home Assistant integration — is that a real want? | You have a Home Assistant setup. "Truck is home / truck is warming up / fuel is low" as HA entities would be a natural background-worker component. Cheap to build, easy to skip. |
| ~~Q9~~ | ~~Dark-only, or light and dark themes?~~ | **Resolved 2026-09-01.** Neither — themes become *tweakable*. Dark is the shipped default; the palette is a set of named tokens the user can adjust. See B1. |
| Q10 | Does DashDeck ever need to run without the truck present — reviewing trips at your desk? | If yes, the simulator becomes a permanent product feature rather than temporary scaffolding, and "desk mode" is a real UI state. |
| Q13 | What actually fills the stage's top bands? | The six-band layout reserves the top 3–4 bands for a large surface. **Android Auto cannot fill it** — it is a phone-projection protocol with no standalone mode and no Windows client; the standalone product (Android Automotive OS) is a head-unit OS, not an app, and installing it collides with C1. So the stage needs real occupants: video, a map component, a media component, the future camera feed. Which of those is first is undecided. |
| Q14 | Do the Climate and Stereo cards conflict with C3? | **Partly, and it matters.** C3 and ADR-0006 both say plainly: *"No takeover of audio, factory camera, or climate."* A **Stereo card is fine** if it drives DashDeck's own playback on the tablet — that is additive. A **Climate card that controls the factory HVAC is not**: it needs an ADR superseding ADR-0006, plus writes to an undocumented Ford network, and it fails several of the five gates as written. Both cards are **parked** as of 2026-09-01; nav slots exist in the mockups, the cards do not. |
| ~~Q15~~ | ~~Is the shell MVVM, and is that imposed on components?~~ | **Resolved 2026-09-01 → [ADR-0011](decisions/ADR-0011-view-contract-and-mvvm.md).** MVVM in the shell with `CommunityToolkit.Mvvm`; components are advised, never required, since they hand back a `FrameworkElement`. `ObservableSignal` in `Abstractions.Wpf` solves dispatcher marshalling and quality rendering once. The same ADR settles the component as **widget + optional full-screen view**. |
| ~~Q17~~ | ~~When a widget's full-screen view opens, what happens to the stage?~~ | **Resolved 2026-09-01. The phone model.** A full-screen view covers the stage entirely and the stage keeps *running* underneath, returning when the view closes. The status strip and nav stay visible over it, so there is always a way back. A pleasant consequence: Settings stops being a special case — every full-screen view takes the same six bands, and "Settings hides the stage" is just what full-screen means. |
| Q18 | Can Google Maps actually fill the stage? | **Connectivity is no longer the issue — there is Wi-Fi in the truck.** What remains is licensing: Google's Maps JavaScript API terms forbid in-vehicle turn-by-turn, there is no desktop SDK, and the licensed Navigation SDK is mobile-only. So the choice is a display-only Google map via the Embed API, or a differently-licensed map (MapLibre, HERE) if real guidance is wanted. See the analysis below. |

## Stage occupants — what already exists

Q13 asks what fills the stage. The instinct not to reinvent anything is right, so this
records what is actually available before anything gets built.

### Google Maps

| Route | Verdict |
|---|---|
| Maps **JavaScript API** in a WebView2 | Renders fine, but the Google Maps Platform terms prohibit real-time navigation and turn-by-turn guidance. The map in the P0.5 mockups is drawn as exactly the thing the licence forbids. |
| Maps **Embed API** (iframe) | Permitted, free, genuinely easy — and display-only. It can show a route; it cannot guide you along one. |
| **Navigation SDK** | The product that *is* licensed for turn-by-turn. Android and iOS only, and needs a commercial agreement. Not available to a Windows app at any price. |
| Embedding the maps.google.com **website** | Against the terms, and fragile. Not an option. |

There is **no Google Maps SDK for Windows or WPF**. That is the whole answer to "can we
just use Google Maps": for a map picture, yes, via the Embed API; for navigation, no.

### If the stage needs real navigation

- **MapLibre GL JS + OpenStreetMap** in a WebView2 — no licence obstacle, but tiles and
  routing are yours to supply (MapTiler or Stadia for tiles; Valhalla, OSRM or GraphHopper
  for routes). Most work, fewest constraints, and can run offline.
- **HERE** — genuinely licensed for in-vehicle navigation and sells to automotive. Costs money.
- **Mapbox** — permissive for display; their navigation product is mobile-first.

### Connectivity — resolved

**There is Wi-Fi in the truck** (confirmed 2026-09-01). The Pro 7 has no cellular of its
own, but it does not need it. Online stage occupants are viable, which removes the argument
that offline capability outranks everything else. It is still worth knowing what degrades
gracefully when the connection drops, since a dash that hangs is worse than one that says
it is offline.

### Nuvio — yes, and there is a clean way

Checked at nuvio.tv, 2026-09-01. Free and open source under **GNU GPLv3**, four repos on
GitHub. Plays via ExoPlayer on mobile and **mpv** on desktop. It supplies no media itself —
catalogues and sources come from addons the user installs.

Three shipping targets matter here:

| Build | Version | Relevance |
|---|---|---|
| **NuvioWeb** | 1.0.3 | **The clean path.** A web app hosts in a WebView2 in-process: resizes properly, obeys the band grid, no window reparenting. |
| Nuvio Desktop | 0.1.22-**alpha** | Its own words: "in alpha and intended for testing, not daily use". Would need `HwndHost` reparenting, and mpv renders to its own GPU surface, which is the fragile case. |
| Nuvio TV | 0.8.12-beta | Built for a TV's remote-control input model, not touch. |

**Licensing is fine, with one line not to cross.** GPLv3 is copyleft, so *linking* Nuvio's
code into DashDeck would put DashDeck under GPLv3 too. Hosting NuvioWeb in a WebView2, or
launching the desktop app as a separate process, does neither — DashDeck is not a
derivative work of a page it displays. Keep it at arm's length and there is no obligation.

So Nuvio on the stage is a **`WebStageOccupant` hosting NuvioWeb**, which is the same
mechanism a maps occupant would use. Not a special case; the second instance of a pattern.

### How an existing program could be hosted at all

Three mechanisms, in order of how well they behave:

1. **WebView2** — hosts any web app in-process. The Evergreen Runtime ships with
   Windows 11, so it costs nothing against constraint C1. This is the clean path for maps,
   video and anything else web-shaped.
2. **`HwndHost` reparenting** — Win32 `SetParent` can pull another running application's
   window into the stage. It works, and it is brittle: DPI changes, focus and input
   handling all get awkward, packaged (UWP) apps refuse outright, and the hosted app's own
   licence may not permit it.
3. **Launch and yield** — hand the whole screen to another app and take it back on exit.
   Crude, but zero risk and sometimes the honest answer.

### Two places the wheel genuinely exists

- **Video: LibVLCSharp.** VLC's engine with a supported WPF integration, LGPL, deploys as
  a folder of DLLs with nothing to install. Plays essentially anything.
- **Media: Windows already knows what is playing.** `GlobalSystemMediaTransportControls`
  exposes the current session — title, artist, artwork, transport control — for Spotify, a
  browser, anything. A Stereo card needs no player of its own, which also keeps it on the
  right side of C3: it reflects the tablet's own audio rather than taking over SYNC 3.

## P0.5 follow-ups

Raised while building the shell. None are blocking; all are real.

| # | Item | Notes |
|---|------|-------|
| F1 | **The status strip does not observe `TransportState`.** | Pull the adapter and it still reads `SYNTHETIC F-150 / SIMULATED`. The mockups have an `ADAPTER LOST — RECONNECTING` banner and the shell should show it; the widgets already go Stale correctly. |
| F2 | **Recovery is unproven.** | `SyntheticTransport.Replug` exists and nothing exercises it. C5 claims disconnect and resume are non-events that recover on their own — that claim is still untested end to end, and it is exactly the sort of thing that quietly is not true. |
| F3 | **Cards are still built by the shell, not loaded from `plugins/`.** | Narrowed 2026-09-02 by [ADR-0015](decisions/ADR-0015-arranged-dashboard.md): they now come from `dashboard.json` and are arranged by the user, so they are no longer constructed in `ShellViewModel`'s constructor and the instance format is settled. What is still missing is a *source* other than a catalog signal — `IDashComponentView` remains a contract nothing implements, which is the least-tested kind. A component's widget will have to honour the page rule: it must be possible to activate and deactivate. |
| F4 | **WPF has no letter-spacing.** | The small uppercase captions read tighter than the design system specifies. Needs a custom text run, or the spec relaxing. First place the design and the framework disagree. |
| F5 | **Two rendering idioms will coexist.** | Binding covers data-driven surfaces; the animated gauges ADR-0001 wants custom-drawn with `DrawingVisual` bypass binding entirely. Accepted in ADR-0011, but nothing has been built the second way yet. |
| F7 | **`ShellViewModel` depends on the concrete `VehicleStack`.** | Which means it cannot be unit tested without starting a real vehicle pipeline. It should take `IVehicleSignals` and a small status interface instead. The band arithmetic got tests; the view model did not, and this is why. |
| F8 | **Stage occupants have no chrome of their own.** | Airspace: `VideoView` and `WebView2` both render into child windows, so no WPF content composes over them at any z-order. That is why the stage now has a **bar** above the occupant rather than a chip floating on it — the bar is the one place stage controls can live. The PARKED chip, video transport and web back/reload all belong there and are not built. |
| F10 | **Auto day/night wants a headlight signal.** | It runs on sunrise and sunset (ADR-0013), which is right most of the time and wrong in a tunnel, an underground car park, or a prairie storm at noon. Lighting status is not in the legislated OBD-II set — it is a Ford body-module message over MS-CAN, so it depends on PID discovery (R2) and on what the Gateway Module passes (R4, Q5). When it exists it is a named signal like any other and only `ThemeService.ResolveAuto` changes. |
| ~~F11~~ | ~~**Settings do not persist.**~~ | **Resolved 2026-09-02 → [ADR-0014](decisions/ADR-0014-custom-accents.md).** Theme and accent are written to `%LOCALAPPDATA%\DashDeck\settings.json` the moment they change — outside the folder `publish.ps1` deletes on every build, so they survive updates. The file is tolerant in both directions, so a file written by an older build loads into a newer one and back. `--theme` and `--accent` preview without persisting. |
| F12 | **The stage occupant is not remembered.** | Settings persist; what was on the stage does not, so every launch comes up empty. The store exists now, so this is a field and a lookup — the reason it is not done is that a stage that reopens a video on ignition may not be wanted, and that is a question rather than an oversight. |
| ~~F9~~ | ~~**The sixth band is empty when the stage takes three.**~~ | **Resolved 2026-09-02 → [ADR-0015](decisions/ADR-0015-arranged-dashboard.md).** The rows grow. `BandGrid.RowsIn` fixes the row height and lets the leftover fall into the bottom gutter, so a three-band stage gets three rows rather than two and a spare band. This also corrected the band grid's documented claim that *n* rows and *n*+1 gutters fill *n* bands exactly — true at two, and only at two. |
| F13 | **Eleven signals is not much of a dash to arrange.** | Paging works and holds as many cards as you like; the catalog defines eleven values and six were already on screen. Nothing here is fixable in the UI — it wants Ford PIDs discovered against the real truck (R2) or non-vehicle sources (B4). Worth stating plainly so the answer is never "invent some cards". |
| F14 | **Reordering is chevrons, not dragging.** | ‹ › on each card in edit mode. Dragging is the gesture people expect, but inside a `Viewbox`-scaled touch surface it is fiddly, and it competes directly with the swipe that turns the page. Chevrons also work with gloves on. Revisit once the page-turn gesture has been used on the tablet in the truck. |
| F18 | **A mount reference can go stale silently.** | Levelling records what "flat and forward" means for the tablet in its cradle (ADR-0017). Bump the mount, or move to a different one, and every attitude and G reading is wrong by that much with nothing on screen to say so. The capture time is stored and not shown; a plausible fix is to surface it, or to notice that the resting attitude has moved while parked. Not solved. |
| F19 | **Five more signal ids that nothing supplies.** | `vehicle.heading`, `vehicle.pitch`, `vehicle.roll`, `vehicle.lateralAccel` and `vehicle.longitudinalAccel` are named in the sensor catalog's `prefer` fields and defined nowhere. That is the design working — the day a PID is found it is a JSON edit — but the catalog now advertises more than the truck has ever delivered, and if discovery fails on all five the tablet fallback is permanent. |
| F16 | **`vehicle.heading` has nothing behind it.** | The compass asks the truck first and always falls back to the tablet, because the catalog has no heading signal and will not until a Ford PID is found (R2, ADR-0016). How badly a magnetometer reads inside a steel cab is unmeasured — first real data comes when the Surface is mounted. If the PID never materialises, the fallback is permanent and the constant is a visible dead end. |
| F17 | **Stage occupants can spend request budget with no suspension rule.** | The compass is the first occupant to declare signals, and it declares for as long as it is on the stage. Cards on an unseen page withdraw (ADR-0015); an occupant hidden behind Settings or a full-screen view does not. Same question as F15, one layer up, and it will matter more as occupants get hungrier. |
| F15 | **Navigating away does not suspend the cards.** | Off-*page* cards withdraw their signal declarations (ADR-0015); cards on the visible page keep theirs while you are in Settings or an applet, where nobody can see them. Suspending on navigation too would reclaim more budget, at the cost of every card showing a placeholder for a second or two on the way back. Which of those is worse is a question, not an oversight. |
| F6 | **A test passed while its bug was present.** | The throughput assertion used `SyntheticFaults.Perfect`, whose zero latency made the measured rate absurd before the cable was even pulled. Worth a sweep for other tests that assert against the perfect fault profile where a realistic one is the point. |

## Decided, not yet built

Direction settled, no ADR yet — these get one in the change that implements them.

| # | Item | Notes |
|---|------|-------|
| ~~B1~~ | ~~**Tweakable themes.**~~ | **Built 2026-09-02 → [ADR-0013](decisions/ADR-0013-theming.md), extended by [ADR-0014](decisions/ADR-0014-custom-accents.md).** Day/Night/Auto plus an accent — four presets or any colour that passes validation, checked as it is typed. The four quality colours are deliberately not themeable, and an accent may not come within 18° of one. Auto runs on sunrise and sunset until a headlight signal exists (F10). |
| B2 | **Six-band layout with a persistent stage.** | Home is six horizontal bands between the status strip and the nav. One band = one widget row. The **stage is a layer, not a screen**: it claims the top *n* bands and keeps running while navigation switches only the region *below* it. Settings is the one destination that takes all six and hides the stage. Replaces the 2-column tile grid. Needs an ADR once the contract for claiming bands — and the stage's separate lifecycle — is settled, and once Q17 is answered. Geometry below. |
| B4 | **Widgets beyond vehicle signals.** | ADR-0012 fixes widgets to named catalog signals for now. Weather, system state and Home Assistant are the obvious next sources, and the clock face already bypasses `IVehicleSignals` entirely to fetch its own — which is the shape of the problem. Needs a provider abstraction broader than the signal bus before it is worth attempting. **More pressing since ADR-0015**: the dash can now hold as many cards as you like and the catalog has eleven things to put on them (F13). **And more pressing again since ADR-0017**, which added five values — heading, pitch, roll and two axes of G — that exist, are catalogued, are quality-tagged, and that a card still cannot bind to. The sensor catalog is most of what this work needs. |
| B5 | **The applet vehicle-data bridge.** | ADR-0012 anticipates it — a compass or gauge applet needs signals — and fixes three rules: declared, read-only forever, and visible to the user. Undesigned beyond that. The moment it exists, an applet's sandbox stops being the whole security story, so it is worth building deliberately rather than on demand. |
| B3 | **Nav is the destination list for the region below the stage.** | It grows as cards are added rather than being a fixed set. **Eased 2026-09-02**: the cells were 228 wide and the strip 168 tall, which read as enormous in the truck. At 130 wide and 108 tall it holds **seven**, so the unsolved overflow question starts at about seven destinations rather than about five. Still unsolved — it is deferred, not answered. |

### B2 geometry — Surface Pro 7, 912 × 1368 portrait

Vertical, and it divides exactly:

| | |
|---|---|
| Status strip | 90 |
| Six bands @ 195 | 1170 |
| Navigation | 108 |
| **Total** | **1368** |

One band is **195** = a **165** widget row plus a **20** gutter — though n rows need 185n + 20,
which equals 195n only at n = 2, so `BandGrid.RowsIn` is the authority. Horizontally: **31** side
margin, **20** gutter, **three 270-wide columns** (31 + 270 + 20 + 270 + 20 + 270 + 31 = 912).

Widget sizes: 1×1 is 270 × 165 · 2×1 is 560 × 165 · 3×1 is 850 × 165 · 1×2 is 270 × 350.
Nav cells are **130** wide, seven across, centred. Stage sizes: 4/6 = 780 · 3/6 = 585 · 2/6 = 390 · 1/6 = 195.

**Navigation was 168 and is now 108.** Four buttons at 228 × 168 measured fine on a desk and
were a target the size of a hand in the truck. The 60 pixels went to the bands, which is why
a widget row is 165 rather than 155.

16:9 video at 912 wide needs 513, so it still fits a 3/6 stage — now with a 72px letterbox —
the property that made 3/6 the video size on the Pro 9 survives the change.

> The P0.5 mockups are drawn at the old 960 × 1440 and are **not** yet re-cut to these
> numbers. They remain valid for layout, hierarchy and the design system; only the
> absolute pixel values are stale.
