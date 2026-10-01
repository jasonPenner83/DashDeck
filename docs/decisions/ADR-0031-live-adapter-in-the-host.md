# ADR-0031 — The Host can run on the real adapter, and falls back to the simulator

**Status:** Accepted · 2026-10-01
**Builds on:** ADR-0003 (transport/adapter split) · ADR-0005 (mock-first)

## Context

Bring-up (ADR-0030) gave the project a working hardware path — `SerialPortTransport`,
`ElmAdapter` against a real OBDLink EX, a measured request ceiling — but it was reachable
only from `DashDeck.BringUp`. `VehicleStack` hard-coded `SyntheticTransport`, so the shell
ran on the simulated truck **even with the adapter plugged in**.

The plumbing existed and the app could not reach it.

## Decision

`VehicleStack` gains `StartLiveAsync(port)` alongside `StartSyntheticAsync(drive)`, and a
`StartAsync(adapterPort, drive)` that picks between them:

- **A COM port configured in Settings → Vehicle** (or a `--port` argument, which wins) →
  run on the real adapter, values flagged `Live`.
- **Empty, the default** → run the synthetic truck, values flagged `Simulated`.
- **Configured but it does not come up** → fall back to the synthetic truck and record why
  in `FallbackReason`.

## Reasoning

**Why falling back rather than failing.** Most of this tablet's life is spent nowhere near
the truck (ADR-0005), and a dash that comes up dead on a desk would be worse than one that
comes up simulated and says so. The honesty is carried by the quality flags and the SIM
badge, not by refusing to run. But a configured port that fails is *reported* rather than
swallowed — coming up simulated when you plugged an adapter in and expected real data is
exactly the confusion the quality flags exist to prevent.

**Why the SIM badge had to change.** It was hard-coded in `MainWindow.xaml`, with a comment
saying it is never removed however tight the bar gets. That was right while the app could
only ever be simulated. It is wrong now: **a badge reading SIM over live data is worse than
no badge, because it teaches you to ignore it.** It is bound to `IsSimulated`, and that
makes it load-bearing — in the truck it is the only thing distinguishing a simulated dash
from a real one.

**Why the baud rate is negotiated.** The EX ships at 115200, its STN2232 reaches 2 Mbps,
and FORScan raises it. Opening at the wrong rate does not fail — it returns mojibake that
reads as a broken adapter. `BaudNegotiator` already solved this for the bring-up tool, so
the Host reuses it rather than making the user get it right.

**What this says about ADR-0003.** `StartLiveAsync` is about ten lines, and nothing above
the transport changed: not the catalog, not the arbiter, not the state bus, not a single
component, not one view. That split was made on the argument that the physical link could
be swapped late. This is the bill arriving, and it was cheap.

## Consequences

- `VehicleStack.Synthetic` becomes **nullable**. The ground-truth figures and the
  unplug/replug diagnostics it backs are simulation-only — a real truck has no "pretend the
  cable came out" button and no exact fuel figure to compare against. Both now no-op on a
  live stack rather than pretending.
- Settings gains an adapter COM port beside the tank size, following the `GpsSerialPort`
  precedent already in the file. It applies at the next launch, like the tank.
- `LinkState` reads from the transport interface rather than the concrete simulator, so the
  `ADAPTER LOST — RECONNECTING` banner works for a real unplugged cable, which is the case
  it was written for.
- **Not verified at runtime.** This was authored on Linux, which can compile the WPF
  projects but cannot run them. The live path has never been exercised against the truck
  from inside the app — only from `DashDeck.BringUp`, which shares every layer below the
  transport. First launch with a port configured is the real test.
