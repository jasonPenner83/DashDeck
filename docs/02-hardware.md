# DashDeck — Hardware

> **Nothing has been purchased yet.** This document is research and a recommendation, not
> a bill of materials. Development proceeds on mock data until an adapter is bought
> (see outline §5a); this exists so the decision is ready when you want it.

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

## Recommended adapter: OBDLink MX+

Given the interview constraints — Bluetooth preferred, possible USB via a Surface dock
later, Intel SP9, no drivers on a personal device — the **OBDLink MX+** is the pick.

- Built on the **STN2120** interpreter, which is ELM327 command-compatible while adding
  an extended `ST` command set for filtering and monitor modes.
- Speaks **both HS-CAN and MS-CAN** with electronic switching, which the pinout table
  above makes non-negotiable. It is also what makes it the de-facto FORScan adapter for
  Ford owners.
- **Bluetooth, and documented on Windows** — it pairs as a serial COM port (PIN `0000`),
  which is precisely what `BluetoothSerialTransport` needs. No driver install, so
  constraint C1 holds.
- Bi-directional, which the Phase 3 action work will eventually need.

**Alternative — OBDLink EX (USB).** Cheaper and faster, no pairing to go wrong, but wired
every single time you get in the truck. Worth considering only if you commit to the dock.

**What to avoid:** generic ELM327 clones. Most are HS-CAN only, which forfeits everything
on MS-CAN, and their firmware is unreliable under sustained polling.

### The constraint to internalise

An ELM/STN adapter is a **text-protocol device**, not a raw CAN interface. Every request
is an ASCII command and every response an ASCII string, over a Bluetooth serial link.
Sustained throughput is on the order of **10–20 requests per second in total** — shared
across every component in the app.

That is entirely adequate for the trip computer, which needs a handful of signals at
1–4 Hz. It is nowhere near enough for smooth 30 Hz gauges. This single fact is why the
Request Arbiter exists (architecture §4), and why the synthetic vehicle deliberately
simulates the same ceiling: components built on mock data must hit the same wall they
will hit in the truck.

The escape hatch, if raw bus access is ever needed, is a proper CAN interface behind the
same `IVehicleAdapter` contract. Nothing above the adapter layer would change.

## The computer: Surface Pro 9 (Intel)

- **Intel, not SQ3/ARM** — confirmed in the interview. Vendor drivers would work if ever
  needed, which removes an entire class of risk. We still prefer the driverless COM-port
  path, because constraint C1 says we do not install drivers on a personal tablet.
- Portrait mount, ~2880×1920 at 267 PPI. High DPI throughout; the UI is vector, with no
  bitmap assets at fixed sizes.
- **Two USB-C ports only, no USB-A.** Power should go over Surface Connect so a USB-C
  port stays free — one more argument for Bluetooth as the daily path.
- Sleep/resume will kill COM ports. The transport layer treats this as routine (risk R3).

## Open hardware questions

Tracked in [`04-open-questions.md`](04-open-questions.md): mount hardware, power in the
cab, and whether the dock scenario is real enough to build the USB transport early.

## Sources

- [Ford OBD-II pinout — pinoutguide.com](https://pinoutguide.com/CarElectronics/ford_obd_2_pinout.shtml)
- [FORScan forum — OBD2 connector pins](https://forscan.org/forum/viewtopic.php?f=4&p=8940)
- [FORScan forum — choosing an ELM327-compatible adapter](https://forscan.org/forum/viewtopic.php?t=6142)
- [2019 F-150 gateway module and connector detail — JustAnswer](https://www.justanswer.com/ford/c786q-show-pinout-obd2-port-gateway-module.html)
- [OBDLink MX+ product page](https://www.obdlink.com/products/obdlink-mxp/)
- [STN2120 interpreter IC — OBD Solutions](https://www.obdsol.com/solutions/chips/stn2120/)
- [Get started with your OBDLink adapter on Windows](https://support.obdlink.com/support/solutions/articles/43000727094-get-started-with-your-obdlink-adapter-windows-)
- [Resolving OBDLink Bluetooth issues on Windows 11](https://support.obdlink.com/support/solutions/articles/43000706058-resolve-bluetooth-connection-issues-on-windows-11)
- [OBDLink Family Reference and Programming Manual (PDF)](https://www.scantool.net/scantool/downloads/682/obdlink_frpm_f.pdf)
