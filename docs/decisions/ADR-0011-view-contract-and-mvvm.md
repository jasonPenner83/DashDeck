# ADR-0011 — The component view contract, and MVVM in the shell

**Status:** Accepted · 2026-09-01
**Builds on:** [ADR-0002](ADR-0002-plugin-model.md) (plugin model) and
[ADR-0010](ADR-0010-abstractions-has-no-ui-dependency.md) (no UI in `Abstractions`).

## Context

P0.5 is the WPF shell, and nothing about the view layer had been decided. ADR-0001 said
only that view logic should stay "thin and out of code-behind" — an intention, not a
pattern. Whatever went into the first file of `DashDeck.Host` was going to set the
convention for everything after it, so it was worth deciding rather than discovering.

Two questions were tangled together:

1. Is the shell MVVM, and does that get imposed on components?
2. What surfaces does a component actually contribute?

They are tangled because the answer to the second constrains the first. `IDashComponentView`
returns a `FrameworkElement` — a *view*. The host receives rendered UI, not state, so it
**cannot** impose a UI pattern on a component even if it wanted to.

## Decision

**A component is two parts, and only the first is required.**

```csharp
public interface IDashComponentView
{
    FrameworkElement CreateWidget();               // required
    FrameworkElement? CreateFullScreen() => null;  // optional
}
```

- A **widget** occupies a band on the home screen. Every visual component has one.
- An **optional full-screen view** opens when the widget is tapped. A component with
  nothing more to show does not override it, and the host makes that widget
  non-interactive rather than opening an empty screen.

**The shell is MVVM. Components are not required to be.** `DashDeck.Host` uses view-models
with `CommunityToolkit.Mvvm` source generators. Component authors are *advised* to do the
same and given a helper that makes it the easy path, but the contract does not require it.

**`ObservableSignal` ships in `DashDeck.Abstractions.Wpf`** — a bindable wrapper around one
signal that declares the demand, observes readings, marshals them onto the dispatcher and
renders quality.

## Reasoning

**Why MVVM in the shell.** Roughly fourteen signals reach a dozen widgets; binding is what
stops that becoming hand-written update code per widget per signal. More importantly it
keeps shell logic testable without a UI thread, which matters for a project that finished
its entire engine against scripted drives with 45 tests and no hardware. Losing that
discipline at the UI boundary would be a real regression. It also satisfies ADR-0001's
stated consequence: view logic out of code-behind is what keeps an Avalonia port expensive
rather than catastrophic.

**Why components are not forced into it.** The alternative — components return view-models
and the host supplies the views — was rejected because it destroys the project's central
goal. A component could then only render what the host's view vocabulary already allows, so
every genuinely new widget shape would require a host change. That is precisely the
opposite of the P4 exit criterion, which is that a component lands *without* host changes.
The ecosystem wins over the single feature's convenience; that is the tie-breaker the
project was set up with.

**Why the full-screen view is optional rather than mandatory.** A fuel-level widget has
nothing more to say. Forcing every component to produce a second surface would fill the app
with screens that exist because the contract demanded them. Optionality also keeps the
simplest possible component genuinely simple — one method.

**Why `ObservableSignal` is host-supplied rather than left to authors.** Two mistakes are
otherwise near-certain, and both are worse on a dash than they sound:

- **Thread marshalling.** All vehicle I/O is on its own worker. Raising
  `INotifyPropertyChanged` from it throws, or silently drops updates.
- **Silent binding failure.** A broken WPF binding logs to debug output and renders
  nothing. On glass that is indistinguishable from a working display showing an unchanging
  number — which is exactly the "confidently wrong" failure this project refuses to accept.

Left to individual authors, each component gets its own chance to get both wrong. Solving
them once, in the assembly every visual component already references, is worth more than
the pattern choice itself.

## Alternatives

- **Enforce MVVM through the contract** (components return view-models, host renders).
  Genuinely attractive: the host would control the design system, guarantee quality-state
  rendering, and components would become UI-framework-free, making a Linux port nearly
  free. Rejected because it requires inventing a declarative UI description language, and
  because it makes the host a bottleneck for every new component — the ecosystem thesis
  fails immediately.
- **No MVVM, code-behind and direct manipulation.** Fewest moving parts and the fastest
  route to something on glass. Rejected because it forecloses the port option ADR-0001
  deliberately preserved, and because shell logic would need a UI thread to test.
- **A single `CreateView()` with the host deciding presentation.** Collapses the widget and
  full-screen distinction. Rejected because a widget must stay glanceable in a moving
  vehicle while a full-screen view can be dense; they are different design problems and
  conflating them produces bad versions of both.

## Consequences

- `CommunityToolkit.Mvvm` becomes a dependency of `DashDeck.Host`. It must **never** reach
  `DashDeck.Abstractions`, which ADR-0002 keeps dependency-free because it is shared across
  every load context. `Abstractions.Wpf` may reference only WPF itself.
- Two rendering idioms will coexist. Binding covers the data-driven surfaces; the animated
  gauges ADR-0001 wants custom-drawn with `DrawingVisual` bypass binding entirely. That is
  accepted, not an oversight — they are different problems.
- The host must handle "widget tapped but no full-screen view" as a normal case, not an
  error.
- Because components hand back views, the host cannot enforce that quality is rendered. It
  can only make the correct path the easy one via `ObservableSignal`, and check it in
  review. Manifest validation cannot catch a component that hides staleness.
- If the contract ever does move to view-models, it is a breaking change to
  `Abstractions.Wpf` and an `apiVersion` major bump. Cheap now, with a component population
  of zero; expensive later. That asymmetry is the reason this was decided before the first
  component was written rather than after.
