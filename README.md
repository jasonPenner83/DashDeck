# DashDeck

[![CI](https://github.com/jasonPenner83/DashDeck/actions/workflows/ci.yml/badge.svg?branch=develop)](https://github.com/jasonPenner83/DashDeck/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A pluggable Windows infotainment ecosystem for a **2019 Ford F-150**.

DashDeck runs on a Surface Pro 7 mounted in portrait in the truck, reads live data from
the vehicle's CAN networks over an OBD-II adapter, and presents it through components you
can add by dropping a folder into `plugins/`.

It sits **alongside** the factory SYNC 3 unit, not in place of it. Close DashDeck and the
truck is a completely normal F-150. It is **read-only**: nothing here writes to the vehicle.

> **Status: P0 engine complete; the WPF shell and component host are live — all on a
> synthetic truck.** The vehicle stack, signal catalog, request arbiter, state bus and a
> synthetic 2019 F-150 are built and tested with no adapter attached. The shell renders a
> user-arranged dash, a stage (clock, compass, gauges, phone projection, video, web and
> native apps) and the nav. Five components load from `plugins/` into isolated,
> fault-contained contexts: Trip Computer, Fuel Economy, Trip Economy, TPMS and Range
> Estimator. The first real adapter (OBDLink EX, wired USB) arrives October 2026.

```bash
dotnet test
dotnet run --project src/DashDeck.Host              # the shell (Windows)
dotnet run --project src/DashDeck.DebugConsole -- cold-start-city --seconds 60
```

## Contributing

Contributions are welcome — especially **components**, which are the point of the project.
Read [CONTRIBUTING.md](CONTRIBUTING.md) first; it covers setup, the branch model, the rules
that aren't obvious from the code, and how to propose an architectural change. Security
issues go through [SECURITY.md](SECURITY.md), not public issues.

## Start here

| Document | What it covers |
|---|---|
| [Project outline](docs/00-project-outline.md) | Goals, constraints, non-goals, phases, risks |
| [Architecture](docs/01-architecture.md) | The layer stack and the contracts between layers |
| [Hardware](docs/02-hardware.md) | 2019 F-150 CAN specifics, adapter recommendation |
| [**Writing a component**](docs/writing-a-component.md) | The practical guide: build, deploy and verify one |
| [Component SDK](docs/03-component-sdk.md) | The contract and the reasoning behind it |
| [Open questions](docs/04-open-questions.md) | What is still undecided |
| [Hardware bring-up](docs/07-bringup.md) | Getting the OBDLink EX talking, on a desk and in the truck |
| [Development setup](docs/06-development-setup.md) | What to install, how to run it, conventions |
| [Releases & branching](docs/05-releases-and-branching.md) | `main`/`develop`, versioning, how a build reaches the truck |
| [Deploy notes](docs/deploy-notes.md) | What is in the tablet build now, and what to test in the truck |
| [Decisions](docs/decisions/) | ADRs — why each call was made |

## The idea in one paragraph

Everything below the components exists to make components easy to write. A component
subscribes to **named signals** (`vehicle.speed`, `engine.fuelRate`) and knows nothing
about PIDs, adapters or transports. It *declares* what data it needs and at what rate; a
central arbiter merges every component's declaration into a single polling plan, because
an OBD-II adapter has a hard request ceiling shared by the whole app. Swap the adapter for
a raw CAN interface later and two layers change — no component changes at all.

## Current focus

Development runs on a **synthetic 2019 F-150**. The simulator models realistic warm-up,
dropped responses and a conservative throughput ceiling, so components hit the same walls
in mock data that they will hit in the truck. Next is bring-up on the real adapter:
measuring the true request ceiling and what the Gateway Module passes on MS-CAN.

## License

[MIT](LICENSE). Third-party components are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Disclaimer

A personal, non-commercial project that talks to a vehicle network. **Not a safety device
and never to be relied on as one.** Don't operate it while driving. Not affiliated with,
endorsed by or sponsored by Ford Motor Company; "Ford", "F-150" and "SYNC" are trademarks of
their owners and are used here only to describe the vehicle the software targets.
