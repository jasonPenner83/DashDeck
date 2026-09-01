# ADR-0010 — `DashDeck.Abstractions` carries no UI dependency

**Status:** Accepted · 2026-09-01
**Amends:** the component interface sketch in `03-component-sdk.md`

## Context

The SDK sketch had `IDashComponent` returning WPF types:

```csharp
FrameworkElement CreateTile();
FrameworkElement CreateScreen();
```

Building P0 showed the problem immediately. A `FrameworkElement` return type forces
`DashDeck.Abstractions` to target `net10.0-windows` with `UseWPF`, which contradicts the
requirement that it stay "small, stable and dependency-free" — and would have made the
entire engine, and every one of its tests, unbuildable anywhere but Windows.

## Decision

Split the contract in two:

- **`DashDeck.Abstractions`** (`net10.0`) — data and service contracts only: signals,
  quality, priorities, clock, storage, settings. No UI types of any kind.
- **`DashDeck.Abstractions.Wpf`** (`net10.0-windows`) — the view surface, added when the
  host is built. A headless component never references it.

## Reasoning

The split was found by building rather than by design review, which is the point: the
architecture doc asserted Abstractions was dependency-free while the interface sketch
quietly made that impossible. Compiling it settled the argument.

It also buys three things worth having:

- **The whole engine is testable on any OS.** P0 was built and verified on Linux with no
  Windows machine involved. That is not a convenience — it is what made mock-first
  development possible at all.
- **Headless components cost nothing.** A Home Assistant bridge or a trip logger has no UI
  and should not drag in a UI framework.
- **The Avalonia escape hatch in ADR-0001 stays real.** A future port would replace
  `Abstractions.Wpf` and the shell; the vehicle stack, the catalog, the arbiter and every
  headless component would be untouched.

## Consequences

- `IDashComponent` splits: lifecycle and data in the core contract, view creation in the
  WPF contract.
- A component manifest declares which surfaces it offers, so the host only asks for a view
  from components that have one.
- `DashDeck.Abstractions` must never gain a UI reference. This is worth a CI check once the
  host exists, because the regression would be silent and expensive.
