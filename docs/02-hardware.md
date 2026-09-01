# DashDeck — Hardware

> **Adapter selected, not yet purchased.** The link is **wired USB** and the adapter is
> the **OBDLink EX** (ADR-0007). Development still proceeds entirely on mock data until it
> arrives (see outline §5a).

## The vehicle: 2019 Ford F-150

The truck runs multiple CAN networks bridged by a **Gateway Module (GWM)** mounted under
the driver-side instrument panel. The GWM is the only module able to translate messages
between the networks, forwarding signals between the high-speed and medium-speed buses on
request.

At the standard OBD-II diagnostic connector:

| Pins | Bus | Rate | Carries |
|------|-----|------|---------|
| 6 / 14 | **HS-CAN** | 500 kbps | Powertrain, transmission, ABS — the legislated OBD-II data |
| 3 / 11 | **MS-CAN** | 125 kbps | Body, comfort, TPMS, HVAC, instrument cluster |

This matters more than it looks. Most of what makes DashDeck interesting over SYNC 3 —
per-wheel TPMS pressures, door and latch state, drivetrain mode — lives on **MS-CAN**. A
cheap adapter that only speaks HS-CAN cannot see any of it. **MS-CAN capability is a hard
requirement, not a nice-to-have.**

How much the GWM filters from the OBD-II port on this specific truck is unknown until
measured (risk R4). The fallback, if it proves restrictive, is a behind-dash tap directly
onto the buses — a real wiring job, and explicitly a last resort.

## Chosen adapter: OBDLink EX (USB)

**Decision made 2026-09-01: wired USB, not Bluetooth** (ADR-0007). The pick is the
**OBDLink EX**, ~$47–70 USD.

### Why the EX

| Property | Why it matters here |
|---|---|
| **Electronic bus switching**, simultaneous HS-CAN + MS-CAN | The decisive property. Cheap adapters use a **physical toggle switch** to change buses. A dash showing engine data *and* tire pressures needs both buses interleaved continuously — no software fixes a switch you have to reach down and flip. OBDLink claims up to 20× the throughput of toggle-switch adapters on this basis. |
| **STN2230** interpreter | ELM327-compatible plus the extended `ST` command set, which is what `IVehicleAdapter` targets. |
| Firmware **co-developed with the FORScan project** | The Ford-specific edge cases have been found by someone else already. |
| FTDI USB bridge | The best-supported virtual COM port path on Windows. |
| 3-year warranty | |

### Runner-up: Vgate vLinker FS

Roughly half the price, auto HS/MS-CAN switching, FORScan-recommended — with an asterisk.
FORScan removed vLinker USB adapters from its recommended list in February 2025 over a
compatibility problem on newer laptops, and reinstated them on 30 May 2025 after a
verified fix. A reasonable choice if budget outweighs wanting the fewest unknowns.

### The trap: OBDLink SX

Same brand, similar name, similar price, and **HS-CAN only**. It cannot see MS-CAN at
all — precisely the half of the feature set the adapter is being bought for. This mistake
is made constantly. Do not buy the SX.

### Consequences for DashDeck

**Two buses, one adapter, one plan.** Every signal definition in the catalog declares its
bus (`hs` or `ms`). The request arbiter must interleave across both, and
`AdapterCapabilities` reports whether simultaneous access is available. An adapter without
electronic switching would make a large part of the signal catalog permanently
unreachable, which is why this is a hardware requirement and not a preference.

**The throughput ceiling is no longer a Bluetooth serial limit.** The ~10–20 requests per
second figure that motivated the arbiter (ADR-0004) is what an ELM-class adapter sustains
over *Bluetooth SPP*. USB removes that bottleneck; the ceiling becomes the STN chip and
the bus itself. **No number is claimed here until it is measured on the truck during
P1.5** — the simulator keeps the conservative Bluetooth-era ceiling until then, because a
simulator that is too generous is worse than one that is too harsh. The practical effect
is that live gauges move from "probably not viable" to "measure it and decide." The
arbiter remains necessary regardless: it is what lets components share whatever the real
ceiling turns out to be.

**One driver, and constraint C1 softens slightly.** The EX requires **FTDI VCP drivers**.
They normally arrive through Windows Update, but OBDLink explicitly says to install them
before first connect, and a red LED on the adapter means they did not take. This is a
Microsoft-signed, cleanly uninstallable USB serial driver rather than anything invasive —
an accepted, deliberate softening of "no drivers on the personal tablet", taken in
exchange for eliminating the Bluetooth reconnect problem (R3). It is recorded in ADR-0007
rather than quietly absorbed.

**Physical fit.** The EX terminates in **USB-A**. The Surface Pro 9 has neither USB-A nor
a spare port to lose, so the link runs through the Surface dock or a USB-C adapter. This
is what makes the dock scenario real rather than hypothetical, and it resolves Q2 — the
USB transport is now the primary one and gets built first.

## The computer: Surface Pro 9 (Intel)

- **Intel, not SQ3/ARM** — confirmed in the interview. Vendor drivers would work if ever
  needed, which removes an entire class of risk. We still prefer the driverless COM-port
  path, because constraint C1 says we do not install drivers on a personal tablet.
- Portrait mount, ~2880×1920 at 267 PPI. High DPI throughout; the UI is vector, with no
  bitmap assets at fixed sizes.
- **Two USB-C ports only, no USB-A.** Power should go over Surface Connect so a USB-C
  port stays free — one more argument for Bluetooth as the daily path.
- Sleep/resume can still invalidate a COM port handle, so the transport continues to treat
  reconnection as routine — but with USB this is a handle to re-acquire rather than a
  Bluetooth link to re-establish, which is a substantially smaller problem than R3
  originally described.

## Open hardware questions

Tracked in [`04-open-questions.md`](04-open-questions.md): mount hardware, power in the
cab, and whether the dock scenario is real enough to build the USB transport early.

## Sources

- [Ford OBD-II pinout — pinoutguide.com](https://pinoutguide.com/CarElectronics/ford_obd_2_pinout.shtml)
- [FORScan forum — OBD2 connector pins](https://forscan.org/forum/viewtopic.php?f=4&p=8940)
- [FORScan forum — choosing an ELM327-compatible adapter](https://forscan.org/forum/viewtopic.php?t=6142)
- [2019 F-150 gateway module and connector detail — JustAnswer](https://www.justanswer.com/ford/c786q-show-pinout-obd2-port-gateway-module.html)
- [OBDLink EX product page](https://www.obdlink.com/products/obdlink-ex/)
- [OBDLink EX on scantool.net](https://www.scantool.net/obdlink-ex/)
- [OBDLink EX support — FTDI driver setup](https://www.obdlink.com/support/ex/)
- [OBDLink EX Windows quick start guide](https://www.obdlink.com/windows_qsg_ex/)
- [FORScan forum — OBDLink EX](https://forscan.org/forum/viewtopic.php?t=11845)
- [FORScan forum — installing USB drivers for OBDLink EX](https://forum.forscan.org/viewtopic.php?t=19001)
- [Vgate vLinker FS (FORScan HS/MS-CAN auto switch)](https://www.amazon.com/Vgate-vLinker-Adapter-FORScan-MS-CAN/dp/B094Z7PBLS)
- [Best FORScan adapters, 2026 review — OBDadvisor](https://obdadvisor.com/best-forscan-adapter-review/)
- [OBDLink MX+ product page (Bluetooth alternative, not chosen)](https://www.obdlink.com/products/obdlink-mxp/)
- [STN2120 interpreter IC — OBD Solutions](https://www.obdsol.com/solutions/chips/stn2120/)
- [Get started with your OBDLink adapter on Windows](https://support.obdlink.com/support/solutions/articles/43000727094-get-started-with-your-obdlink-adapter-windows-)
- [Resolving OBDLink Bluetooth issues on Windows 11](https://support.obdlink.com/support/solutions/articles/43000706058-resolve-bluetooth-connection-issues-on-windows-11)
- [OBDLink Family Reference and Programming Manual (PDF)](https://www.scantool.net/scantool/downloads/682/obdlink_frpm_f.pdf)
