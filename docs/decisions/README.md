# Architecture Decision Records

One file per decision. Numbered, immutable once accepted — a decision that changes gets a
new ADR that supersedes the old one, rather than an edit. The point is to preserve *why*,
including the reasoning that later turns out to be wrong.

| ADR | Decision | Status |
|-----|----------|--------|
| [0001](ADR-0001-ui-stack.md) | .NET 9 + WPF for the shell | Accepted |
| [0002](ADR-0002-plugin-model.md) | In-process plugins from a `plugins/` folder | Accepted |
| [0003](ADR-0003-transport-abstraction.md) | Transport and adapter are separate layers | Accepted |
| [0004](ADR-0004-request-arbiter.md) | Components declare signals; an arbiter schedules them | Accepted |
| [0005](ADR-0005-mock-first.md) | Synthetic vehicle is the primary data source until hardware exists | Accepted |
| [0006](ADR-0006-additive-not-replacement.md) | DashDeck is additive to SYNC 3, and read-only before Phase 3 | Accepted |
| [0007](ADR-0007-usb-link-and-adapter.md) | Wired USB link, OBDLink EX adapter | Accepted |
