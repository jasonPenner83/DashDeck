# DashDeck — Architecture

Companion to [`00-project-outline.md`](00-project-outline.md). This document covers the
layers, the contracts between them, and the reasoning behind the non-obvious choices.

## Solution layout

```
DashDeck.sln
├─ src/
│  ├─ DashDeck.Abstractions/     ← the SDK contract. net10.0, no UI, no dependencies.
│  ├─ DashDeck.Vehicle/          ← transports + adapters (ELM/STN, recording, replay)
│  ├─ DashDeck.Core/             ← signal catalog, request arbiter, state bus, poll loop
│  ├─ DashDeck.Simulator/        ← synthetic F-150 + scripted drives
│  ├─ DashDeck.DebugConsole/     ← runnable harness; no UI needed to exercise the stack
│  ├─ DashDeck.Storage/          ← SQLite, per-component scopes            (P1)
│  ├─ DashDeck.Abstractions.Wpf/ ← the view half of the SDK, net10.0-windows (P0.5)
│  └─ DashDeck.Host/             ← WPF shell, component host, settings UI  (P0.5)
├─ catalog/                      ← signal definitions as JSON, not code
├─ components/
│  └─ DashDeck.Components.TripComputer/                                    (P1)
├─ tests/
└─ docs/

Everything except `Abstractions.Wpf` and `Host` targets plain `net10.0` and builds on any
OS — see ADR-0010. That is what let the entire engine be built and tested before any
Windows machine was involved.
```

`DashDeck.Abstractions` is load-bearing. It must stay small, stable, dependency-free, and
semantically versioned, because every component in existence is compiled against it.
Anything that changes often does not belong in it.

## Layer 1 — Transport

```csharp
public interface IVehicleTransport : IAsyncDisposable
{
    TransportState State { get; }
    IObservable<TransportState> StateChanged { get; }
    Task ConnectAsync(CancellationToken ct);
    Task<ReadOnlyMemory<byte>> ExchangeAsync(ReadOnlyMemory<byte> request, CancellationToken ct);
    IAsyncEnumerable<ReadOnlyMemory<byte>> StreamAsync(CancellationToken ct);
}
```

A transport moves bytes and nothing else. It knows no protocol.

| Implementation | Purpose |
|---|---|
| `SyntheticTransport` | The simulated truck. **The default until hardware exists.** |
| `ReplayTransport` | Plays a recorded or scripted session back at real time or faster. |
| `BluetoothSerialTransport` | OBDLink-class adapter paired as an SPP COM port. |
| `UsbSerialTransport` | Same adapter family over USB, for the Surface dock scenario. |

Bluetooth and USB are the *same* adapter protocol over different pipes, which is exactly
why transport and adapter are separate layers — leaning Bluetooth now costs nothing if a
dock is added later.

**Disconnection is a normal state, not an error.** Every transport reconnects with
backoff on its own. Nothing above this layer is allowed to require an app restart to
recover from an unplugged cable or a Surface waking from sleep.

## Layer 2 — Vehicle adapter

```csharp
public interface IVehicleAdapter
{
    AdapterCapabilities Capabilities { get; }
    Task InitializeAsync(CancellationToken ct);
    Task<PidResponse> RequestAsync(PidRequest request, CancellationToken ct);
    IAsyncEnumerable<CanFrame> MonitorAsync(CanFilter filter, CancellationToken ct);
}
```

Speaks the adapter's command protocol: initialisation, protocol/bus selection (HS-CAN vs
MS-CAN), request framing, response parsing, error and timeout semantics.
`AdapterCapabilities` is how the rest of the system discovers what it is talking to —
whether MS-CAN is reachable, whether raw monitoring is supported, and the measured
request ceiling. A future raw-CAN adapter implements this same interface and reports
much higher capabilities; nothing above changes.

## Layer 3 — Signal catalog

The catalog is **data, not code** — a set of JSON definitions mapping a source (an OBD-II
PID, or a CAN arbitration ID plus bit range) to a named, typed, unit-carrying signal.

```jsonc
{
  "id": "vehicle.speed",
  "name": "Vehicle Speed",
  "source": { "kind": "obd-pid", "mode": "01", "pid": "0D", "bus": "hs" },
  "decode": { "type": "u8", "offset": 0, "scale": 1.0, "unit": "km/h" },
  "range": { "min": 0, "max": 255 },
  "defaultRateHz": 4
}
```

This is deliberate. Ford's interesting values — trans temp, per-wheel TPMS, real coolant,
odometer — are undocumented and will be found by trial and error against the truck
(risk R2). Discovering a new signal must be a config edit, never a recompile. It also
means the synthetic vehicle and the real truck are driven by the *same* catalog, so a
signal that exists in simulation is guaranteed to have a real definition waiting for it.

## Layer 4 — Request arbiter

The adapter is a single serialised resource with a hard throughput ceiling (risk R1).
Components therefore never request anything; they **declare** what they need:

```csharp
ctx.Signals.Require("vehicle.speed",      SignalPriority.High,   rateHz: 4);
ctx.Signals.Require("engine.fuelRate",    SignalPriority.Normal, rateHz: 2);
ctx.Signals.Require("ambient.temp",       SignalPriority.Low,    rateHz: 0.1);
```

The arbiter merges every live declaration into one deduplicated polling plan: two
components wanting `vehicle.speed` produce one poll at the higher of the two rates.
When the plan exceeds the adapter's measured ceiling, low-priority signals are degraded
first and subscribers are told their effective rate — they are never silently starved.
Signals for components that are not currently visible drop to background priority.

This layer is the single most important design decision in the project. Without it, the
ecosystem thesis collapses the moment a third component is installed.

## Layer 5 — Vehicle state bus

An in-memory snapshot of every known signal plus a pub/sub stream:

```csharp
public interface IVehicleSignals
{
    SignalValue<T> Current<T>(string signalId);
    IObservable<SignalValue<T>> Observe<T>(string signalId);
    IDisposable Require(string signalId, SignalPriority priority, double rateHz);
}
```

Every `SignalValue` carries its value, unit, acquisition timestamp, and a quality flag
(`Live`, `Stale`, `Unavailable`, `Simulated`). **Components must render staleness rather
than hide it** — a dash showing a confidently wrong number is worse than one showing a
dash. `Simulated` is included so mock data is never mistaken for real data on screen.

The bus coalesces per subscriber: the UI gets at most one update per frame, while the
recorder gets everything.

## Layer 6 — Services

- **Storage** — SQLite. Each component gets an isolated scope; none can read another's.
- **Trip recorder** — opens a trip on first movement, closes after an idle timeout
  (there is no ignition signal, per constraint C5).
- **Session recorder** — captures raw traffic to replayable logs. Always available,
  because a five-minute drive that reproduces a bug is worth more than any debugger.
- **Settings**, **logging**, **navigation/shell state**.

## Layer 7 — Component host

Discovery, manifest validation, API version negotiation, permission granting, lifecycle,
and layout slot assignment. See [`03-component-sdk.md`](03-component-sdk.md).

## Layer 8 — Shell UI

Portrait-first (constraint C4):

- **Top status strip** — connection and data-quality state, gear, speed, clock, alerts.
- **Home** — a scrolling grid of component tiles.
- **Component screen** — a tile tapped full-screen.
- **Bottom navigation** — persistent, in the bottom third. In a portrait tablet mounted
  in a truck dash, that is the only band comfortably reachable from the driver's seat;
  putting navigation at the top would be a genuine ergonomic error.

The visual language is a custom design system over the Fluent theme WPF gained in .NET 9. No stock
control survives contact with a touch target sized for a moving vehicle, so "modern
looking" is our job, not the framework's (ADR-0001).

## Threading

- The UI thread renders. It never performs I/O, never blocks, never awaits a transport.
- Transport and adapter own a dedicated worker; all vehicle I/O is serialised through it.
- The state bus marshals to the dispatcher only at the final subscription hop.
- Every call into component code is time-boxed; a component that hangs is stopped, not
  tolerated (see outline §7).

## Testability

- The synthetic vehicle and replay transport make the entire stack testable with no
  hardware — which, for now, is the only way it can be tested at all.
- Scripted drives with known outcomes give exact expected values, so fuel-economy maths
  can be asserted rather than eyeballed.
- The signal catalog is validated in CI: every referenced signal must exist, and every
  definition must decode its recorded fixture correctly.
