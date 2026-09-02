# DashDeck

A pluggable Windows infotainment ecosystem for a **2019 Ford F-150**.

DashDeck runs on a Surface Pro 7 mounted in portrait in the truck, reads live data from
the vehicle's CAN networks over an OBD-II adapter, and presents it through components you
can add by dropping a folder into `plugins/`.

It sits **alongside** the factory SYNC 3 unit, not in place of it. Close DashDeck and the
truck is a completely normal F-150.

> **Status: P0 engine complete, no UI yet.** The vehicle stack, signal catalog, request
> arbiter, state bus and a synthetic 2019 F-150 are built and tested — with no adapter and
> no truck. The WPF shell is next, and needs Windows.

```bash
dotnet test
dotnet run --project src/DashDeck.DebugConsole -- cold-start-city --seconds 60
```

## Start here

| Document | What it covers |
|---|---|
| [Project outline](docs/00-project-outline.md) | Goals, constraints, non-goals, phases, risks |
| [Architecture](docs/01-architecture.md) | The layer stack and the contracts between layers |
| [Hardware](docs/02-hardware.md) | 2019 F-150 CAN specifics, adapter recommendation |
| [Component SDK](docs/03-component-sdk.md) | How to write a component |
| [Open questions](docs/04-open-questions.md) | What is still undecided |
| [Development setup](docs/06-development-setup.md) | What to install, how to run it, conventions |
| [Releases & branching](docs/05-releases-and-branching.md) | `main`/`develop`, versioning, how a build reaches the truck |
| [Decisions](docs/decisions/) | ADRs — why each call was made |

## The idea in one paragraph

Everything below the components exists to make components easy to write. A component
subscribes to **named signals** (`vehicle.speed`, `engine.fuelRate`) and knows nothing
about PIDs, adapters or Bluetooth. It *declares* what data it needs and at what rate; a
central arbiter merges every component's declaration into a single polling plan, because
an OBD-II adapter sustains only about 10–20 requests per second for the whole app. Swap
the Bluetooth adapter for a raw CAN interface later and two layers change — no component
changes at all.

## Current focus

Development runs entirely on a **synthetic 2019 F-150** until an adapter is bought. The
simulator models realistic warm-up, dropped responses and the same throughput ceiling as
the real thing, so components hit the same walls in mock data that they will hit in the
truck.

First component: a **fuel economy and trip computer with history** — including tank
calibration against real fill-ups, which is the thing the factory cluster can't do.

## Disclaimer

A personal, non-commercial project that talks to a vehicle network. Not a safety device
and never to be relied on as one.
