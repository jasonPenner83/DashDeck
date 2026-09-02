# Architecture Decision Records

One file per decision. Numbered, immutable once accepted — a decision that changes gets a
new ADR that supersedes the old one, rather than an edit. The point is to preserve *why*,
including the reasoning that later turns out to be wrong.

| ADR | Decision | Status |
|-----|----------|--------|
| [0001](ADR-0001-ui-stack.md) | WPF for the shell (runtime version superseded by 0009) | Accepted |
| [0002](ADR-0002-plugin-model.md) | In-process plugins from a `plugins/` folder | Accepted |
| [0003](ADR-0003-transport-abstraction.md) | Transport and adapter are separate layers | Accepted |
| [0004](ADR-0004-request-arbiter.md) | Components declare signals; an arbiter schedules them | Accepted |
| [0005](ADR-0005-mock-first.md) | Synthetic vehicle is the primary data source until hardware exists | Accepted |
| [0006](ADR-0006-additive-not-replacement.md) | DashDeck is additive to SYNC 3, and read-only before Phase 3 | Accepted |
| [0007](ADR-0007-usb-link-and-adapter.md) | Wired USB link, OBDLink EX adapter | Accepted |
| [0008](ADR-0008-branching-and-versioning.md) | `main`/`develop` branching, three independent version streams | Accepted |
| [0009](ADR-0009-target-net10.md) | Target .NET 10 LTS (supersedes the version in 0001) | Accepted |
| [0010](ADR-0010-abstractions-has-no-ui-dependency.md) | `DashDeck.Abstractions` carries no UI dependency | Accepted |
| [0011](ADR-0011-view-contract-and-mvvm.md) | Component = widget + optional full-screen; MVVM in the shell only | Accepted |
| [0012](ADR-0012-widgets-and-applets.md) | Declarative widgets and sandboxed web applets for third parties (supersedes 0002 in part) | Accepted |
| [0013](ADR-0013-theming.md) | Bounded theming: day/night, one curated accent, quality colours fixed | Accepted |
| [0014](ADR-0014-custom-accents.md) | Custom accents, allowed by validation rather than curation (supersedes one clause of 0013); settings persist in `%LOCALAPPDATA%` | Accepted |
| [0015](ADR-0015-arranged-dashboard.md) | The dash is a user-arranged list of cards, flowed into pages; only the visible page declares signals | Accepted |
| [0016](ADR-0016-vehicle-first-heading.md) | Vehicle data first, device sensors as a declared and visible fallback | Accepted |
| [0017](ADR-0017-sensor-catalog.md) | A second catalog for tablet sensors, with a required mount reference | Accepted |
