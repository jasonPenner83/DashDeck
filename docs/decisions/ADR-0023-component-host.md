# ADR-0023 — The component host: discovery, load, lifecycle, containment

**Status:** Accepted · 2026-09-03
**Implements:** [ADR-0002](ADR-0002-plugin-model.md) (in-process plugins from `plugins/`) and
tier 1 of [ADR-0012](ADR-0012-widgets-and-applets.md) (first-party code, unchanged).
**Builds on:** [ADR-0010](ADR-0010-abstractions-has-no-ui-dependency.md) (the split contract),
[ADR-0008](ADR-0008-branching-and-versioning.md) (`apiVersion` is the compatibility promise),
[ADR-0015](ADR-0015-arranged-dashboard.md) (a component inherits the instance/activation shape),
[ADR-0004](ADR-0004-request-arbiter.md) and [ADR-0005](ADR-0005-mock-first.md).
**Resolves in part:** F3 — components now load from `plugins/`. What is deferred is named below.

## Context

ADR-0002 chose in-process plugins and ADR-0012 kept that for first-party code, but neither
built it: the SDK was a document (`03-component-sdk.md`) and `IDashComponentView` an interface
nothing implemented. ADR-0015 then settled the part the host would inherit — a card is an
*instance* naming a *source*, and whatever a component contributes must be activatable and
deactivatable because the page rule applies to it too — and explicitly left the host as "a
shape rather than a blank page". This is the host.

The contract is the thing that ossifies (ADR-0008): once components exist, a breaking change to
`DashDeck.Abstractions` invalidates every one of them and costs an ADR. So the first component is
built *through* the public contract, headless, to exercise the shape before anyone depends on it.

## Decision

### The contract, in `DashDeck.Abstractions`

`IDashComponent` — `Id`, `InitializeAsync`, and the four visibility transitions `StartAsync` /
`StopAsync` / `SuspendAsync` / `ResumeAsync`. `IComponentContext` hands a component exactly what
it may reach: `Signals`, `Storage`, `Settings`, `Logger`, `Clock`, and `Actions` — the last
`null` until Phase 3, a guarantee expressed as a nullable reference rather than a comment
(ADR-0006). `IComponentStorage`, `IComponentSettings`, `IComponentLogger` and the empty
`IVehicleActions` are the sub-contracts; `ComponentApi.Version` is the one place the contract
version is written. All of it is UI-free, so a headless component builds off Windows (ADR-0010).

### Load shallow, then deep

The host walks `plugins/`, and for each folder does the cheap, code-free checks first:

1. read and shape-validate `component.json`;
2. reject an `apiVersion` outside the served range (`1.0`–`ComponentApi.Version`: same major,
   minor no higher than the host's);
3. reject a declared signal the catalog does not define.

Only then is the assembly mapped. An incompatible or malformed component never runs a line of
its code, and every failure becomes a `LoadedComponent` with a stated `Reason`, never an
exception that stops the dash coming up — a folder in `plugins/` is untrusted input.

### One collectible context each, sharing only the contract

Each component loads into its own collectible `AssemblyLoadContext`. The context defers exactly
the contract assemblies (`DashDeck.Abstractions`, `DashDeck.Abstractions.Wpf`) to the host's
default context and loads everything else from the component's folder. This is not a detail:
a type is identified by its assembly *and its load context*, so a component carrying its own
copy of the contract would implement a different `IDashComponent` and the cast that turns the
loaded object into a component would fail. The manifest's `id` is authoritative and the code's
`Id` is checked to agree, so a component cannot mis-scope its storage.

### A wall around every call

Every call into component code goes through `GuardedComponent`: it is time-boxed (5s), its
exceptions are caught, and a component that throws or hangs is counted against and, after three
faults, disabled — its state rendered rather than hidden (a "component stopped" surface, when
there is UI). In-process a hung call cannot truly be killed; the guarantee is that the host
stops *waiting* on it and stops *calling* a repeat offender, which is the honest limit of
containment without a process boundary (ADR-0002's rejected alternative) and enough to keep one
wedged component from freezing the dash.

### Isolated context services

Storage is a folder per component under `%LOCALAPPDATA%\DashDeck\components\<id>\`, a file per
key, written-and-renamed so a power cut leaves the old value rather than a truncated one.
Settings read one JSON object, tolerantly. The logger tags every line with the component's id,
because a component cannot be trusted to identify itself in a shared log.

### Headless now, widgets next

A component with no widget has no page to gate it, so the host starts it immediately and it runs
whenever the vehicle is present. The first component — a trip odometer integrating
`vehicle.speed` — is exactly this, and it works: it accumulates distance, persists it, and
resumes on the next launch, proving the whole path end to end.

## Deliberately deferred

- **Dash placement.** Hosting a component's `IDashComponentView.CreateWidget` as an `IDashSlot`
  in the paged grid, with activation tied to the visible page — the next step, and the reason
  the first component is headless.
- **The settings editor**, `statusItem`, hot-reload file-watching, and permission *enforcement*
  beyond parsing. `IVehicleActions` stays empty until Phase 3.
- **Shipping `plugins/` in `publish.ps1`** and a solution-level build that rebuilds components.
  Dev finds the repo `plugins/`; a packaged build will need them beside the executable.
- **The CI check that `DashDeck.Abstractions` gains no UI reference** (ADR-0010's open
  consequence) — now more valuable, since the contract just grew.

## Reasoning

**Validate before loading, because the cheapest rejection is the one that maps nothing.** An
incompatible component is a common thing to find — an old plugin against a new host — and it
should cost a JSON read, not an assembly load and a type search.

**The contract-unification subtlety is the whole game.** It is the one thing that turns "load a
DLL" into "load a *component*", and it is invisible until it is wrong, at which point it reads as
an impossible `IDashComponent`-to-`IDashComponent` cast failure. Writing it down here is cheaper
than rediscovering it.

**Containment over isolation, knowingly (ADR-0002).** The population of first-party authors is
still one, so the guard's honest in-process limits are the right trade. When third-party *code*
arrives, ADR-0012 already says the answer is a process boundary or the web tier — not a bigger
wall around an in-process call.

## Consequences

- **`apiVersion` is now real.** The host serves a range and enforces it; a major bump invalidates
  every component and needs its own ADR (ADR-0008), which is now a live constraint, not a
  future one.
- **`DashDeck.Abstractions` grew the component contract and must stay dependency-free.** The
  ADR-0010 CI check is no longer optional-feeling.
- **The instance/source shape from ADR-0015 gets its third source next.** `CardSpec.Source`
  gains `Component`, and a `ComponentWidgetSlot : IDashSlot` carries a component's view into the
  same paged, activated flow the signal and sensor cards already live in.
- **A first component exists and is built through the SDK**, so the contract is exercised — and
  its rough edges are findable — before anyone else depends on it.
