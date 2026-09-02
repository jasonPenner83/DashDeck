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
| Q17 | When a widget's full-screen view opens, what happens to the stage? | Raised by ADR-0011. Two readings, both coherent. **(a)** The full-screen view fills the region *below* the stage, which shrinks to its floor — the map stays visible, nothing is ever hidden. **(b)** It covers the stage entirely and the stage keeps *running* underneath, returning when the view closes — the phone model. (b) is likely what "full-screen" ought to mean, but it needs saying out loud, because it decides whether the nav strip stays visible over a full-screen view and how you get back. |

## Decided, not yet built

Direction settled, no ADR yet — these get one in the change that implements them.

| # | Item | Notes |
|---|------|-------|
| B1 | **Tweakable themes.** | The design system is already a token set (surfaces, text ramp, accent, four quality colours). Expose the tokens in settings rather than shipping a second hand-built theme. Constraint: quality colours must stay distinguishable from the accent and from each other, and contrast has to survive whatever is chosen — so tweaking is bounded, not a free colour picker on every token. Supersedes Q9. |
| B2 | **Six-band layout with a persistent stage.** | Home is six horizontal bands between the status strip and the nav. One band = one widget row. The **stage is a layer, not a screen**: it claims the top *n* bands and keeps running while navigation switches only the region *below* it. Settings is the one destination that takes all six and hides the stage. Replaces the 2-column tile grid. Needs an ADR once the contract for claiming bands — and the stage's separate lifecycle — is settled, and once Q17 is answered. Geometry below. |
| B3 | **Nav is the destination list for the region below the stage.** | It grows as cards are added rather than being a fixed set. Four fit comfortably at 228 wide; beyond about five the strip needs to scroll or paginate, which is undecided and becomes urgent as soon as a second card lands. |

### B2 geometry — Surface Pro 7, 912 × 1368 portrait

Vertical, and it divides exactly:

| | |
|---|---|
| Status strip | 90 |
| Six bands @ 185 | 1110 |
| Navigation | 168 |
| **Total** | **1368** |

One band is **185** = a **165** widget row plus a **20** gutter. Horizontally: **31** side
margin, **20** gutter, **three 270-wide columns** (31 + 270 + 20 + 270 + 20 + 270 + 31 = 912).

Widget sizes: 1×1 is 270 × 165 · 2×1 is 560 × 165 · 3×1 is 850 × 165 · 1×2 is 270 × 350.
Nav cells are 228 wide. Stage sizes: 4/6 = 740 · 3/6 = 555 · 2/6 = 370 · 1/6 = 185.

16:9 video at 912 wide needs 513, so it still fits a 3/6 stage with a 42px letterbox —
the property that made 3/6 the video size on the Pro 9 survives the change.

> The P0.5 mockups are drawn at the old 960 × 1440 and are **not** yet re-cut to these
> numbers. They remain valid for layout, hierarchy and the design system; only the
> absolute pixel values are stale.
