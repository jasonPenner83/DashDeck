# ADR-0007 — Wired USB link, OBDLink EX adapter

**Status:** Accepted · 2026-09-01
**Amends:** ADR-0003 (transport priority), constraint C1, risks R1 and R3

## Context

ADR-0003 split transport from adapter precisely so the physical link could be chosen
late. The initial lean was Bluetooth, on the reasoning that a tablet carried in and out of
the truck shouldn't need a cable plugged in every time.

Against that: the Bluetooth SPP path carries the app's single worst throughput constraint
(R1) and its most likely recurring annoyance (R3, reconnect across sleep/resume).

## Decision

The vehicle link is **wired USB**, and the adapter is the **OBDLink EX**.
`UsbSerialTransport` becomes the primary transport and is built first.
`BluetoothSerialTransport` may never be built.

## Reasoning

**Electronic bus switching decided it.** The 2019 F-150 puts powertrain data on HS-CAN and
body data — TPMS, doors, drivetrain mode — on MS-CAN. Cheaper adapters change buses with a
*physical toggle switch*. For a diagnostic session that is an annoyance; for a dash that
displays engine and body data on the same screen it is disqualifying, because no software
can flip a switch the driver has to reach for. The EX switches electronically and reaches
both buses simultaneously. This is a hardware requirement, not a preference.

**It retires the worst risk in the project.** Bluetooth SPP reconnect flakiness (R3) was
assessed as near-certain to bite. A USB handle to re-acquire after sleep is a far smaller
problem.

**It lifts the throughput ceiling.** The ~10–20 requests/second figure behind ADR-0004 is
what an ELM-class adapter sustains over *Bluetooth serial*. USB removes that bottleneck.

**The cable cost is smaller than it looked.** The tablet already has to be seated and
powered in the truck. A dock or USB-C adapter was going to be involved regardless, so the
marginal cost of the wired link is close to zero.

## Alternatives

- **OBDLink MX+ (Bluetooth)** — the previous recommendation, and still a good adapter.
  Rejected because its advantage is only the absent cable, which the mount and power
  situation largely negates.
- **Vgate vLinker FS (USB)** — roughly half the price with auto HS/MS-CAN switching.
  Rejected on unknowns rather than merit: FORScan delisted vLinker USB adapters in
  February 2025 over a compatibility problem on newer laptops and reinstated them on
  30 May 2025 after a verified fix. A sound budget choice if cost outweighs certainty.
- **OBDLink SX (USB)** — **HS-CAN only.** Cannot see MS-CAN at all, forfeiting a large
  part of the signal catalog. Named here explicitly because the SX and EX are similarly
  named and priced, and are confused constantly.

## Consequences

- **Constraint C1 is knowingly softened.** The EX requires FTDI VCP drivers. They usually
  arrive via Windows Update, but OBDLink says to install them before first connect, and a
  red adapter LED means they did not take. One Microsoft-signed, cleanly uninstallable USB
  serial driver is accepted; nothing more invasive is.
- **The dock is now on the critical path.** The EX terminates in USB-A and the SP9 has
  none, so a dock or USB-C adapter is required. This resolves Q2.
- **R1 is downgraded but the arbiter stays.** ADR-0004 is unaffected: central scheduling is
  what lets components share whatever ceiling exists, and the ceiling is now unknown rather
  than known-bad. **No new number is assumed** — the simulator keeps the conservative
  Bluetooth-era ceiling until a real one is measured in P1.5, because a simulator that is
  too generous produces components that fail on the truck.
- Every signal definition must declare its bus (`hs` / `ms`), and the arbiter must
  interleave across both. `AdapterCapabilities` reports whether simultaneous access exists.
- Live gauges move from "probably not viable" to "measure and decide" — deliberately not
  promoted further than that until Q12 is answered with real data.
