# DashDeck — Development Setup

## What you need

| | |
|---|---|
| **.NET 10 SDK** | https://dotnet.microsoft.com/download/dotnet/10.0 — the LTS release (ADR-0009). |
| **Visual Studio 2022 Community** | Free. Needed for the WPF shell: XAML hot reload and the designer matter when sizing touch targets for a moving vehicle. Install the *.NET desktop development* workload. |
| **VS Code** *(optional)* | Fine for everything except XAML. Install the **C# Dev Kit** extension. |
| **Git** | |

No OBD-II adapter is needed. None of P0 or P1 requires one.

## Get it running

```bash
git clone https://github.com/jasonPenner83/DashDeck.git
cd DashDeck
git checkout develop

dotnet test
dotnet run --project src/DashDeck.DebugConsole -- cold-start-city --seconds 60
```

If the tests pass and the console prints live signals, the whole engine works.

## The debug console

The P0 harness. Not a stand-in for the shell — it exists so the entire stack below the UI
can be run, watched and recorded on any machine, with no truck, no adapter and no Windows.
Everything it drives is the real pipeline.

```bash
# Drives: cold-start-city, highway-cruise, towing-pull, idle
dotnet run --project src/DashDeck.DebugConsole -- highway-cruise --seconds 120

# Record a session to a replayable log
dotnet run --project src/DashDeck.DebugConsole -- cold-start-city --record captures/drive.jsonl

# Replay it — nothing above the transport knows the difference
dotnet run --project src/DashDeck.DebugConsole -- --replay captures/drive.jsonl --seconds 30
```

It prints the polling plan before it starts, including which signals the arbiter degraded
and why. When something looks wrong on a dash, that plan is the first place to look.

## Where things are

| Project | Target | Builds on |
|---|---|---|
| `DashDeck.Abstractions` | `net10.0` | any OS |
| `DashDeck.Vehicle` | `net10.0` | any OS |
| `DashDeck.Core` | `net10.0` | any OS |
| `DashDeck.Simulator` | `net10.0` | any OS |
| `DashDeck.DebugConsole` | `net10.0` | any OS |
| `DashDeck.Abstractions.Wpf` *(P0.5)* | `net10.0-windows` | **Windows only** |
| `DashDeck.Host` *(P0.5)* | `net10.0-windows` | **Windows only** |

The split is deliberate (ADR-0010): the engine and all its tests build anywhere, which is
what allowed P0 to be finished before any Windows machine was involved. Keep it that way —
a UI reference sneaking into `Abstractions` would be a silent and expensive regression.

## Adding a signal

Edit `catalog/signals.obd2-standard.json`. No rebuild, no code:

```jsonc
{
  "id": "engine.oilTemp",
  "name": "Engine Oil Temperature",
  "bus": "Hs",
  "pid": 92,
  "decode": { "byteOffset": 0, "byteLength": 1, "signed": false, "scale": 1, "offset": -40, "unit": "°C" },
  "defaultRateHz": 0.5,
  "min": -40, "max": 210
}
```

`min`/`max` are a correctness guard, not decoration: a wrong decode spec usually produces a
wildly out-of-range number, and out-of-range readings are dropped rather than displayed.
Ford-specific PIDs go in a separate file once discovered on the truck, so trial and error
never churns the known-good standard set.

## Conventions worth knowing before you write code

- **Never `DateTimeOffset.Now`.** Take `IClock`. Wall-clock reads cannot be replayed or
  tested against a scripted drive.
- **Components declare signals, they never poll.** If you want a PID, the catalog is
  missing a definition.
- **Render quality.** Every value carries `Live` / `Stale` / `Unavailable` / `Simulated`.
  A confidently wrong number on a dash is worse than a blank one.
- **Nothing blocks the UI thread.** The dash must not freeze while someone is driving.

`TreatWarningsAsErrors` is on. That is intentional — a warning ignored in a vehicle app is
a warning ignored at 100 km/h.

## Branches

`develop` for day-to-day work; `main` is the build that goes on the truck tablet. Never
commit directly to `main`. See [`05-releases-and-branching.md`](05-releases-and-branching.md).
