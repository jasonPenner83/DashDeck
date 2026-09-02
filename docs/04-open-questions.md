# DashDeck — Open Questions

Live list. Resolved items move to an ADR in [`decisions/`](decisions/).

## Blocking nothing right now (mock-data phase)

| # | Question | Notes |
|---|----------|-------|
| ~~Q1~~ | ~~Bluetooth MX+ or wired EX?~~ | **Resolved 2026-09-01 → ADR-0007.** Wired USB, OBDLink EX. Electronic bus switching was the deciding property. |
| ~~Q2~~ | ~~Is the dock scenario real enough to build `UsbSerialTransport` early?~~ | **Resolved by Q1.** The EX is USB-A and the SP9 has none, so the dock or a USB-C adapter is now on the critical path. `UsbSerialTransport` is the primary transport and gets built first; Bluetooth may never be built at all. |
| Q11 | Dock, or a bare USB-C adapter? | Only matters for how the tablet is powered and seated in the truck. Software is identical either way. |
| Q3 | Mount hardware and power in the cab. | Affects nothing in software, but decides portrait geometry and whether the tablet charges while docked. |
| Q4 | Does this truck support the fuel-rate PID (0x5E)? | Unanswerable until an adapter exists. MAF fallback plus tank calibration is designed to cover either answer (risk R5). |
| Q5 | How much does the Gateway Module filter at the OBD-II port? | Measured on first bring-up. Determines whether MS-CAN signals are reachable without a behind-dash tap (risk R4). |
| Q12 | What is the *real* sustained request ceiling over USB? | **Machinery built, answer still unknown.** `VehicleService.MeasuredRequestsPerSecond` measures it continuously from request service time and feeds the arbiter's budget, so the number will be read off the truck rather than estimated. Until then the simulator holds the pessimistic 15 req/sec figure. It decides whether live gauges are viable. |

## Product questions for you

| # | Question | Notes |
|---|----------|-------|
| Q6 | After the trip computer, which component do you actually want next? | The outline assumes gauges as the SDK-proving second component, but that is a placeholder, not a decision. |
| Q7 | Should trip history stay strictly local, or sync somewhere? | Local-only is the default and the simplest. Worth deciding before the storage schema sets. |
| Q8 | Home Assistant integration — is that a real want? | You have a Home Assistant setup. "Truck is home / truck is warming up / fuel is low" as HA entities would be a natural background-worker component. Cheap to build, easy to skip. |
| ~~Q9~~ | ~~Dark-only, or light and dark themes?~~ | **Resolved 2026-09-01.** Neither — themes become *tweakable*. Dark is the shipped default; the palette is a set of named tokens the user can adjust in settings. See "Decided, not yet built" below. |
| Q10 | Does DashDeck ever need to run without the truck present — reviewing trips at your desk? | If yes, the simulator becomes a permanent product feature rather than temporary scaffolding, and "desk mode" is a real UI state. |
| Q13 | What actually fills the stage's top bands? | The 6-band layout reserves the top 3–4 bands for a large surface. **Android Auto cannot fill it** — it is a phone-projection protocol with no standalone mode and no Windows client; the standalone product (Android Automotive OS) is a head-unit OS, not an app, and installing it collides with C1. So the stage needs real occupants: video, a map component, a media component, the future camera feed. Which of those is first is undecided. |

| Q14 | Do the Climate and Stereo cards conflict with C3? | **Partly, and it matters.** C3 and ADR-0006 both say plainly: *"No takeover of audio, factory camera, or climate."* A **Stereo card is fine** if it drives DashDeck's own playback on the tablet — that is additive. A **Climate card that controls the factory HVAC is not**: it needs an ADR superseding ADR-0006, plus writes to an undocumented Ford network, and it fails several of the five gates as written. Both cards are **parked** as of 2026-09-01; nav slots exist in the mockups, the cards do not. |

| Q15 | Is the shell MVVM, and is that imposed on components? | **Undecided, and it needs deciding before P0.5 starts.** ADR-0001 only says "keeping view logic thin and out of code-behind", which is an intention, not a pattern. The wrinkle is the SDK: `IDashComponentView` hands back a `FrameworkElement`, i.e. a *view*, not a view-model — so the host cannot impose MVVM on components even if it uses it internally. Decide (a) MVVM in the shell with `CommunityToolkit.Mvvm` source generators, which must live in `Host`, never in `Abstractions` (ADR-0002 keeps that dependency-free), and (b) whether component authors are merely *advised* to use MVVM or the contract changes to require it. |
| Q16 | Which Surface is the truck tablet? | The docs say **Surface Pro 9 (Intel)**, whose portrait logical size is 960 × 1440 — the size the P0.5 mockups are drawn at. The dev machine as of 2026-09-01 is a **Surface Pro 7** (12.3", 2736 × 1824, 200% scale) giving **912 × 1368** portrait. If the Pro 7 is the truck tablet, the band arithmetic needs redoing: 1368 − 90 status − 180 nav = 1098, which is not divisible by six. Either re-derive the band height for 1098, or confirm the Pro 9 is the target and treat the Pro 7 as a dev box. |

## Decided, not yet built

Direction settled, no ADR yet — these get one in the change that implements them.

| # | Item | Notes |
|---|------|-------|
| B1 | **Tweakable themes.** | The design system is already a token set (surfaces, text ramp, accent, four quality colours). Expose the tokens in settings so the palette can be adjusted rather than shipping a second hand-built theme. Constraints: quality colours must stay distinguishable from the accent and from each other, and contrast has to survive whatever is chosen — so tweaking is bounded, not a free colour picker on every token. Supersedes Q9. |
| B2 | **Six-band layout with a persistent stage.** | Home is six horizontal bands between the status strip and the nav. One band = one widget row (175 + 20 gutter = 195). The **stage is a layer, not a screen**: it claims the top *n* bands and keeps running while navigation switches only the region *below* it. Tapping a widget expands it in place below the stage, which yields bands rather than being replaced. Settings is the one destination that takes all six and hides the stage. Replaces the 2-column tile grid in the P0.5 mockups. Needs an ADR once the component contract for claiming bands — and the stage's separate lifecycle — is settled. |
| B3 | **Nav is the destination list for the region below the stage.** | It grows as cards are added (Stereo, Climate, …) rather than being a fixed set. Four fit comfortably at 240 wide; beyond about five the strip needs to scroll or paginate, which is undecided. |
