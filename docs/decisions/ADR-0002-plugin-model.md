# ADR-0002 — In-process plugins from a `plugins/` folder

**Status:** Accepted · 2026-09-01

## Context

The ecosystem is the point of the project: adding a component should be easy enough that
it actually happens. Options were in-process assemblies, out-of-process components over
IPC, or a hybrid.

## Decision

Each component is a folder in `plugins/` loaded **in-process** into its own collectible
`AssemblyLoadContext`, sharing only `DashDeck.Abstractions` with the host.

## Reasoning

Authoring friction is the thing most likely to kill the ecosystem. In-process is by far
the lowest-friction option: a component is one small project, one manifest, one folder,
with direct WPF views and no serialisation boundary or IPC contract to maintain.
Collectible load contexts also buy hot-reload during development, which compounds.

The population of component authors is one person. Sandboxing against malicious code
solves a problem that does not exist here; the real risk is *buggy* code, and that is
addressed more cheaply by containment than by process isolation.

## Alternatives

- **Out-of-process over IPC** — a crashing component genuinely cannot take down the dash.
  Rejected: it taxes every component with IPC boilerplate and makes UI embedding hard,
  paying a permanent authoring cost to solve a problem containment mostly covers.
- **Hybrid** — the eventual answer if a component ever hosts third-party code. Rejected
  as premature; the layering does not preclude adding an out-of-process host later.

## Consequences

- **A badly behaved component can crash the dash.** This is the real cost and it is
  accepted knowingly. Mitigations: every call into component code is wrapped and
  time-boxed; a faulted component is replaced by a "component stopped" tile; repeated
  faults auto-disable it.
- Components must target the same .NET version as the host.
- `DashDeck.Abstractions` must stay small, stable and dependency-free, because it is
  shared across every load context.
