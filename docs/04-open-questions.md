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
| ~~Q13~~ | ~~What actually fills the stage's top bands?~~ | **Resolved 2026-09-02 → [ADR-0019](decisions/ADR-0019-phone-projection.md).** The stage has six occupants: clock, compass, phone projection, video, and web applets. The original answer — *Android Auto cannot fill it* — was right about the protocol and wrong about the conclusion: there is still no Windows client and Google licenses no receiver for a PC, but a **Carlinkit CPC200 terminates Android Auto and CarPlay in firmware** and hands back H.264 over USB. So DashDeck speaks to the dongle rather than being a head unit. Dongle chosen, not bought; built against a synthetic transport. Costs a second driver exception, which ADR-0019 admits explicitly. |
| Q14 | Do the Climate and Stereo cards conflict with C3? | **Partly, and it matters.** C3 and ADR-0006 both say plainly: *"No takeover of audio, factory camera, or climate."* A **Stereo card is fine** if it drives DashDeck's own playback on the tablet — that is additive. **Climate control is not**: it needs writes to an undocumented Ford network and fails several of the five gates as written. **Climate *display* is built** (2026-10-02, [ADR-0040](decisions/ADR-0040-climate-panel.md)): CLIMATE shows a read-only panel of what the truck reports, from a layout file. Its signals are placeholders until the HVAC module is found, so on the truck it reads dashes. Control stays Phase 3; Stereo stays parked. |
| ~~Q15~~ | ~~Is the shell MVVM, and is that imposed on components?~~ | **Resolved 2026-09-01 → [ADR-0011](decisions/ADR-0011-view-contract-and-mvvm.md).** MVVM in the shell with `CommunityToolkit.Mvvm`; components are advised, never required, since they hand back a `FrameworkElement`. `ObservableSignal` in `Abstractions.Wpf` solves dispatcher marshalling and quality rendering once. The same ADR settles the component as **widget + optional full-screen view**. |
| ~~Q17~~ | ~~When a widget's full-screen view opens, what happens to the stage?~~ | **Resolved 2026-09-01. The phone model.** A full-screen view covers the stage entirely and the stage keeps *running* underneath, returning when the view closes. The status strip and nav stay visible over it, so there is always a way back. A pleasant consequence: Settings stops being a special case — every full-screen view takes the same six bands, and "Settings hides the stage" is just what full-screen means. |
| Q18 | Can Google Maps actually fill the stage? | **Connectivity is no longer the issue — there is Wi-Fi in the truck.** What remains is licensing: Google's Maps JavaScript API terms forbid in-vehicle turn-by-turn, there is no desktop SDK, and the licensed Navigation SDK is mobile-only. So the choice is a display-only Google map via the Embed API, or a differently-licensed map (MapLibre, HERE) if real guidance is wanted. See the analysis below. |
| Q19 | Should a theme bring its own launcher? | A theme already names its stage layout (ADR-0037), so LCARS brings an LCARS stage. The launcher file (ADR-0038) is not tied to a theme: which programs are on this tablet is not a look. But a theme might want its own quick bar — LCARS with an ENGINE and a TOWING button — and that is the case to watch for. Until then, one `launcher.json` for every theme. |

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
| ~~F1~~ | ~~**The status strip does not observe `TransportState`.**~~ | **Resolved 2026-09-03.** `ShellViewModel.Refresh` samples `VehicleStack.LinkState` on the clock beat -- polled on the UI thread rather than driven by the transport's worker-thread `StateChanged`, matching the shell's `DispatcherTimer` idiom. When the link is anything but `Connected` the strip shows an `ADAPTER LOST — RECONNECTING` banner (a fault reads `ADAPTER FAULT`), in the same amber (`#E0B23C`, the Stale colour) the widgets turn as their readings age -- so the banner and the cards agree rather than inventing a fifth signal colour. `LinkState` is read through the transport interface, so a real OBDLink answers it unchanged. |
| ~~F2~~ | ~~**Recovery is unproven.**~~ | **Resolved 2026-09-03.** `EndToEndTests.Plugging_the_adapter_back_in_recovers_without_a_restart` drives the whole path: `engine.rpm` answers, `Unplug` ages it to Stale (and the link reports `Disconnected`), then `Replug` brings it back to a fresh reading with no re-`Require` and no restart -- proving the C5 claim rather than asserting it. The transport's `StateChanged` sequence is checked too: `Disconnected` then `Connected`. `ElmAdapter` turning the transport `IOException` into a timeout is what keeps the worker alive across the gap; the test is what shows it does. |
| ~~F3~~ | ~~**Cards are still built by the shell, not loaded from `plugins/`.**~~ | **Resolved 2026-09-03 → [ADR-0023](decisions/ADR-0023-component-host.md).** The in-process component host discovers `plugins/`, validates the manifest and `apiVersion` before mapping any code, loads each component into its own collectible `AssemblyLoadContext` sharing only the contract, runs a guarded time-boxed lifecycle, and contains faults. A component`s `IDashComponentView` widget is now hosted on the dash as a `ComponentCardViewModel` -- a third `CardSpec.Source` (`Component`) beside Signal and Sensor -- placed in the one ordered arrangement and activated by the visible page (the ADR-0015 rule reaches components through `IDashCard`). The first component, a Trip Computer in `components/TripComputer/`, ships in the default layout and draws its own live distance widget. A card naming a component that is not installed is kept and marked `UNAVAILABLE`, never dropped. **Deferred (small):** reorder/remove/settings-edit of a component card via the editor UI, `statusItem`, hot-reload, and permission enforcement. (`plugins/` now ships in `publish.ps1`.) |
| F4 | **WPF has no letter-spacing.** | The small uppercase captions read tighter than the design system specifies. Needs a custom text run, or the spec relaxing. First place the design and the framework disagree. |
| F5 | **Two rendering idioms will coexist.** | Binding covers data-driven surfaces; the animated gauges ADR-0001 wants custom-drawn with `DrawingVisual` bypass binding entirely. Accepted in ADR-0011, but nothing has been built the second way yet. |
| F7 | **`ShellViewModel` depends on the concrete `VehicleStack`.** | Which means it cannot be unit tested without starting a real vehicle pipeline. It should take `IVehicleSignals` and a small status interface instead. The band arithmetic got tests; the view model did not, and this is why. |
| F8 | **Stage occupants have no chrome of their own.** | Airspace: `VideoView` and `WebView2` both render into child windows, so no WPF content composes over them at any z-order. That is why the stage now has a **bar** above the occupant rather than a chip floating on it — the bar is the one place stage controls can live. The PARKED chip, video transport and web back/reload all belong there and are not built. |
| F10 | **Auto day/night wants a headlight signal.** | It runs on sunrise and sunset (ADR-0013), which is right most of the time and wrong in a tunnel, an underground car park, or a prairie storm at noon. Lighting status is not in the legislated OBD-II set — it is a Ford body-module message over MS-CAN, so it depends on PID discovery (R2) and on what the Gateway Module passes (R4, Q5). When it exists it is a named signal like any other and only `ThemeService.ResolveAuto` changes. |
| ~~F11~~ | ~~**Settings do not persist.**~~ | **Resolved 2026-09-02 → [ADR-0014](decisions/ADR-0014-custom-accents.md).** Theme and accent are written to `%LOCALAPPDATA%\DashDeck\settings.json` the moment they change — outside the folder `publish.ps1` deletes on every build, so they survive updates. The file is tolerant in both directions, so a file written by an older build loads into a newer one and back. `--theme` and `--accent` preview without persisting. |
| F12 | **The stage occupant is not remembered.** | **Direction set 2026-09-03**: the answer is *not* "reopen whatever was there" — a video resuming on ignition is exactly the case that made this a question. Instead the **idle default becomes the gauges stage** (B6), so the stage always has a sensible thing to fall back to and there is nothing arbitrary to restore. Remembering a *chosen* occupant across a single session is still open, but the launch default is settled. |
| ~~F9~~ | ~~**The sixth band is empty when the stage takes three.**~~ | **Resolved 2026-09-02 → [ADR-0015](decisions/ADR-0015-arranged-dashboard.md).** The rows grow. `BandGrid.RowsIn` fixes the row height and lets the leftover fall into the bottom gutter, so a three-band stage gets three rows rather than two and a spare band. This also corrected the band grid's documented claim that *n* rows and *n*+1 gutters fill *n* bands exactly — true at two, and only at two. |
| F13 | **Eleven signals is not much of a dash to arrange.** | **Eased 2026-09-02**: the sensor catalog's five values are now bindable too, so sixteen things can go on a card. Still not a lot, and the real fix is Ford PID discovery against the truck (R2) — which is why the adapter matters more than any of this. |
| F14 | **Reordering is chevrons, not dragging.** | ‹ › on each card in edit mode. Dragging is the gesture people expect, but inside a `Viewbox`-scaled touch surface it is fiddly, and it competes directly with the swipe that turns the page. Chevrons also work with gloves on. Revisit once the page-turn gesture has been used on the tablet in the truck. |
| F20 | **The dongle's H.264 is never decoded.** | The session counts frames and raises them; nothing turns them into a picture. LibVLC will do it — it is already deployed — but feeding a live elementary stream into a `MediaPlayer` is real work and cannot be written against a synthetic dongle that sends no frames. First thing to build when the CPC200 arrives. |
| F21 | **Projected audio has nowhere to go.** | The dongle sends PCM with a routing field, and C3 forbids taking over the truck's audio. Whether projected audio comes out of the tablet, is left to the phone's own Bluetooth link to the truck, or is switchable, is undecided — and it is the question most likely to make phone projection unpleasant to actually use. |
| F18 | **A mount reference can go stale silently.** | Levelling records what "flat and forward" means for the tablet in its cradle (ADR-0017). Bump the mount, or move to a different one, and every attitude and G reading is wrong by that much with nothing on screen to say so. The capture time is stored and not shown; a plausible fix is to surface it, or to notice that the resting attitude has moved while parked. Not solved. |
| F19 | **Vehicle signal ids that nothing supplies.** | `vehicle.heading`, `vehicle.pitch`, `vehicle.roll`, `vehicle.lateralAccel`, `vehicle.longitudinalAccel` — and now `vehicle.latitude`/`vehicle.longitude` (ADR-0027) — are named in `prefer` fields and defined nowhere. That is the design working: the day a PID is found it is a JSON edit. Heading, speed and position now at least have a **phone-GPS** fallback rather than only the tablet's (ADR-0027, easing F16); attitude and G still fall back to the tablet alone, and every one of these `vehicle.*` PIDs remains undiscovered (R2). |
| F16 | **`vehicle.heading` has nothing behind it.** | **Eased 2026-09-08 → [ADR-0027](decisions/ADR-0027-phone-location.md).** The compass now falls back to the **phone's GPS course** (labelled `PHONE · GPS`) while moving, *before* the tablet magnetometer — which in a steel cab is usually the better of the two, so the "how badly does a magnetometer read in a cab" worry matters less. Still open at the truck end: `vehicle.heading` itself is undefined and will be until a Ford PID is found (R2, F19). |
| F17 | **Stage occupants can spend request budget with no suspension rule.** | The compass is the first occupant to declare signals, and it declares for as long as it is on the stage. Cards on an unseen page withdraw (ADR-0015); an occupant hidden behind Settings or a full-screen view does not. Same question as F15, one layer up, and it will matter more as occupants get hungrier. |
| F15 | **Navigating away does not suspend the cards.** | Off-*page* cards withdraw their signal declarations (ADR-0015); cards on the visible page keep theirs while you are in Settings or an applet, where nobody can see them. Suspending on navigation too would reclaim more budget, at the cost of every card showing a placeholder for a second or two on the way back. Which of those is worse is a question, not an oversight. |
| ~~F22~~ | ~~**Audio outlives the stage in one direction and dies in the other.**~~ | **Resolved 2026-09-06 → [ADR-0025](decisions/ADR-0025-occupant-lifetime.md).** The three lifecycles were one rule that was never stated: **an occupant lives as long as it is the stage occupant.** Leaving it — navigating below the stage, or opening a full-screen view over it — keeps it running (the phone model, Q17/B2); *replacing* it ends it, because the stage holds one thing. Both were already the behaviour and both are right. The only real fault was the middle case being **invisible**: a full-screen view hides a running occupant with nothing on screen to say so. Fixed by showing, not by changing the lifecycle — the always-visible status strip now carries a **▶ NAME** pill (`StageRunningButHidden` + `ReturnToStageCommand`) that appears only then, names what is on, and taps back to it. Stop was offered and declined in favour of tap-to-return. F21 (where projected audio comes out) is still open; this decided how long it lasts, not which speaker. |
| F6 | **A test passed while its bug was present.** | The throughput assertion used `SyntheticFaults.Perfect`, whose zero latency made the measured rate absurd before the cable was even pulled. Worth a sweep for other tests that assert against the perfect fault profile where a realistic one is the point. |

## Decided, not yet built

Direction settled, no ADR yet — these get one in the change that implements them.

| # | Item | Notes |
|---|------|-------|
| ~~B1~~ | ~~**Tweakable themes.**~~ | **Built 2026-09-02 → [ADR-0013](decisions/ADR-0013-theming.md), extended by [ADR-0014](decisions/ADR-0014-custom-accents.md).** Day/Night/Auto plus an accent — four presets or any colour that passes validation, checked as it is typed. The four quality colours are deliberately not themeable, and an accent may not come within 18° of one. Auto runs on sunrise and sunset until a headlight signal exists (F10). |
| B2 | **Six-band layout with a persistent stage.** | Home is six horizontal bands between the status strip and the nav. One band = one widget row. The **stage is a layer, not a screen**: it claims the top *n* bands and keeps running while navigation switches only the region *below* it. Settings is the one destination that takes all six and hides the stage. Replaces the 2-column tile grid. Needs an ADR once the contract for claiming bands — and the stage's separate lifecycle — is settled, and once Q17 is answered. Geometry below. |
| ~~B4~~ | ~~**Widgets beyond vehicle signals.**~~ | **Done in part 2026-09-02.** A card now names a `source` — `Signal` or `Sensor` — and can bind to anything in either catalog, so heading, pitch, roll and both axes of G are card values like any other. They cost no request budget and their footer says where they answered from (`TRUCK` / `TABLET · TRUE`) rather than an allocated rate. `ICardValue` is the seam; a component's own source is the third implementation (F3). **Still open:** weather and Home Assistant, which are neither catalog and need a provider abstraction. |
| B5 | **The applet vehicle-data bridge.** | ADR-0012 anticipates it — a compass or gauge applet needs signals — and fixes three rules: declared, read-only forever, and visible to the user. Undesigned beyond that. The moment it exists, an applet's sandbox stops being the whole security story, so it is worth building deliberately rather than on demand. |
| B6 | **A gauges stage, and it is the idle default.** | A stage occupant that shows a cluster of gauges styled after the 2019 F-150's instrument cluster — and then goes past it, adding gauges the cluster does not show (boost, oil temp, trans temp, per-wheel, G, the derived ones). It becomes the **default** the stage falls back to instead of the clock (answering F12's launch question). Built with the second rendering idiom ADR-0011 anticipated — custom-drawn gauges, not data-bound TextBlocks (F5) — so it is also where that idiom finally gets exercised. Reads only from the signal catalog and the sensor catalog, so nothing new is needed below it; the gauges get real numbers the day the adapter lands (R2). |
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

## Answerable now that the adapter exists

Q4 (fuel-rate PID support) and Q12 (the real throughput ceiling) are no longer research
questions — the bring-up tool measures both. See
[`07-bringup.md`](07-bringup.md). Stage 1 runs on a desk with no vehicle; stage 2 in the
truck produces the numbers, and the capture it records lets later work be developed
against real truck data with no truck present.

## Closed by bring-up, 2026-10-01

Measured on the truck with an OBDLink EX (STN2232 v5.12.4) over USB. Capture and full
report in `captures/bringup-20261001-193733.*`.

| # | Question | Answer |
|---|---|---|
| ~~Q4~~ | Does this truck support the fuel-rate PID `0x5E`? | **No — and it does not support MAF `0x10` either**, which was the stated fallback. Both inputs to fuel flow are absent. Resolved by [ADR-0030](decisions/ADR-0030-fuel-flow-without-maf.md): speed-density from MAP, IAT, RPM and lambda, with tank calibration promoted from refinement to requirement. |
| ~~Q12~~ | What is the real sustained request ceiling over USB? | **~19 requests/second** — mean 52.5 ms round trip, min 47.8 ms, p95 71.4 ms, 0 of 60 failed. Note FORScan's "15 ms min delay" is its inter-command gap, not a round trip; the vehicle's response time dominates. Risk R1 therefore stands: USB bought far less than hoped, and high-rate gauges need **request batching**, not a faster link. |
| ~~Q5~~ | How much does the Gateway Module filter at the OBD-II port? | Not restrictive for our purposes. HS-CAN answers fully and the adapter accepts the MS-CAN switch, so no behind-dash tap is needed. Ford body PIDs on MS-CAN still need discovering — they are not advertised in the standard support bitmaps. |

### Opened by bring-up

| # | Question | Notes |
|---|----------|-------|
| Q13 | Which Ford mode 22 PIDs carry fuel flow, oil temperature and transmission temperature? | FORScan reads all three, so they exist. `PidRequest` already formats 16-bit mode 22 commands, so only the PID numbers are missing. Recovering fuel flow directly would make ADR-0030's speed-density estimate a fallback rather than the primary path, and transmission temperature is the most valuable gauge on a truck that tows. **The next hardware-side task.** The tools for it are on the tablet now (ADR-0035): find the module with SCAN FOR MODULES, sweep its identifiers, DEFINE and TEST one while the value changes. |
| Q14 | Can several PIDs be batched into one request? | The single highest-leverage performance question. ELM/STN accepts multiple PIDs in one mode 01 message, returning several values per round trip. At 52 ms per round trip, batching six signals into one request would turn ~19 requests/second into roughly 100 values/second and change what the dashboard can show. Needs an adapter and arbiter change, and verification on the truck. |
| Q15 | What is this truck's engine displacement and learned VE correction? | Inputs to ADR-0030's speed-density calculation. Belong on the vehicle profile (ADR-0029), not hard-coded. **Displacement answered 2026-10-01 → [ADR-0033](decisions/ADR-0033-vin-lookup-and-vehicle-packs.md):** it comes from the VIN (`VehicleProfile.EngineDisplacementLitres`) — 2.7 L on this truck, not the 3.5 earlier comments assumed. The VE correction is still open. |
