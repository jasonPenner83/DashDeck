# ADR-0001 — .NET 10 + WPF for the shell

**Status:** Accepted · 2026-09-01
**Superseded in part by:** [ADR-0009](ADR-0009-target-net10.md) — the runtime version is .NET 10, not .NET 9. The framework choice below stands unchanged.

## Context

The brief was "modern looking, snappy responses" on a Surface Pro 9 (Intel) mounted in
portrait, running as an ordinary app on a personal device that gets carried in and out of
the truck. Candidates were WPF, Avalonia 11, WinUI 3, and an Electron/web frontend over a
.NET backend.

## Decision

**.NET 10 + WPF**, with a custom design system built on top of the Fluent theme that
shipped with .NET 9.

## Reasoning

"Modern looking" is a design-system problem, not a framework one. No stock control
survives being resized for a touch target used in a moving vehicle, so every surface gets
re-templated regardless of framework. The framework choice therefore turns on the
unglamorous parts, and WPF wins those:

- **Cold start.** Fastest of the four. This is an app launched by hand each time you get
  in the truck, so startup is felt on every single use.
- **Rendering.** Retained mode on a dedicated render thread; `DrawingVisual` custom-draws
  animated gauges cheaply.
- **Trodden paths.** Twenty years of recipes for serial and USB interop, `D3DImage` and
  `WriteableBitmap` for a future camera feed, borderless windows, and
  `AssemblyLoadContext` plugin loading. Every one of those is on the critical path.

The Surface Pro 9 being a *personal, non-dedicated* device was raised as a possible
reason to reconsider, and does not change the answer. It changes the deployment posture
(no kiosk, no drivers, no services — constraint C1), which WPF satisfies as easily as the
alternatives.

## Alternatives

- **Avalonia 11** — close runner-up. Skia/GPU compositor and CSS-like styling are genuinely
  faster to build a design system in. Rejected only because its advantage is
  cross-platform insurance against a Linux SBC that is not in the plan, and it has fewer
  trodden paths for camera, serial and windowing specifics.
- **WinUI 3** — most modern native look, but packaging identity and windowing fight a
  full-screen custom-chrome app, and cold start is worse.
- **Electron + .NET backend** — easiest component authoring story, at a RAM and boot cost
  that is exactly wrong on a tablet.

## Consequences

- Windows-only, permanently. Accepted; the brief is a Windows app.
- The design system is our work, not the framework's. Budget for it.
- **If a Linux SBC ever enters the picture, revisit immediately.** The layering means a
  port would touch the shell and component views but not the vehicle stack — expensive,
  not catastrophic. Keeping view logic thin and out of code-behind preserves that option
  at no cost today.
