# ADR-0012 — Two ways to extend: declarative widgets, sandboxed applets

**Status:** Accepted · 2026-09-02
**Supersedes in part:** [ADR-0002](ADR-0002-plugin-model.md) — the in-process plugin model
stands for first-party components, but its central premise no longer holds and it is no
longer the answer for third-party code.
**Builds on:** [ADR-0011](ADR-0011-view-contract-and-mvvm.md) (component = widget + optional
full-screen view) and B2 (the stage is a layer, not a screen).

## Context

ADR-0002 chose in-process plugins loaded into collectible `AssemblyLoadContext`s, and was
explicit about what that rested on:

> The population of component authors is **one person**. Sandboxing against malicious code
> solves a problem that does not exist here; the real risk is *buggy* code.

That premise is now void. The project is going public on GitHub with the expectation that
other people write extensions. ADR-0002 anticipated this precisely — it rejected the hybrid
model as *"the eventual answer if a component ever hosts third-party code. Rejected as
premature."* It is no longer premature.

Two things sharpened the decision.

**The cost ADR-0002 knowingly accepted changes character.** "A badly behaved component can
crash the dash" is a fair trade when you wrote the component. Published, it becomes: a
stranger's code runs in-process with the user's privileges, in a moving vehicle, able to
read their files and reach their network. `AssemblyLoadContext` isolates *assemblies*, not
*trust*. It was never a sandbox and was never sold as one.

**The audience arithmetic is brutal.** A C# plugin author for this needs C# *and* WPF *and*
Windows *and* a 2019 F-150 *and* a Surface mounted in it. That intersection is close to
empty; the car-PC hobbyist world is overwhelmingly Android, Linux and Python. The extension
mechanism is not what limits the audience — the platform is — but it decides how wide the
door is for whoever does turn up.

## Decision

Three tiers, with different rules, because the things being extended are different.

### 1. First-party components — in-process, unchanged

Code written by the maintainer keeps ADR-0002's model: a folder in `plugins/`, its own
collectible load context, `DashDeck.Abstractions` as the only shared assembly. The
friction argument that motivated it is still correct for the person who owns the repository.
The trip computer (P1) is built this way and P1 is unaffected.

### 2. Widgets — declarative data, not code

A widget is a JSON description, not an assembly:

```jsonc
{
  "id": "com.example.transtemp",
  "label": "TRANS TEMP",
  "signal": "transmission.fluidTemp",   // a named signal from the catalog
  "priority": "normal",
  "rateHz": 0.5,
  "format": "0",
  "size": "1x1"
}
```

**Widgets bind to vehicle signals only, for now.** The catalog is already data (ADR-0004),
so adding a *source* is already a JSON edit rather than a recompile — the two halves meet
naturally. Widening widgets to non-vehicle sources (weather, system state, Home Assistant)
is a planned direction, not part of this decision.

There is no code to load, so there is no trust decision to make, no crash to contain, and
no `apiVersion` to break. A malformed widget fails validation and says so.

### 3. Applets — stage occupants, authored as web apps

An applet occupies the stage and is an HTML/JS application hosted in a WebView2, exactly as
the existing Nuvio, Maps and Stremio occupants already are. Third-party stage extensions are
web by default.

**Applets may need vehicle data** — a compass, a gauge cluster, a trip map all do — and that
arrives through an explicit host-mediated bridge, **not** by handing the page the state bus.
When built, the bridge must be:

- **Declared.** An applet lists the signals it wants, the way a component does. Undeclared
  signals are not readable.
- **Read-only, permanently.** Never `IVehicleActions`, not even after Phase 3. A web page
  does not get to touch the vehicle.
- **Visible.** The user can see which signals an applet reads before enabling it.

Not built yet. Recorded here so it is designed rather than discovered.

## Reasoning

**Widgets are data because most widgets *are* data.** Look at the six on the dash today:
each is a label, a signal id, a rate and a number format. Nothing about them wants to be a
program. Expressing them as data removes the entire trust question for the case that needs
it least, and makes the most common contribution — "show me trans temp" — something a person
can write without a compiler.

**Applets are web because the audience is JavaScript, not WPF**, and because browser process
isolation supplies for free exactly what `AssemblyLoadContext` never did: a crashing applet
cannot take the dash with it, and a malicious one cannot read the filesystem. The mechanism
is already proven — `WebStageOccupant` carries Nuvio, Maps and Stremio, and the third needed
no new code at all.

**Splitting them is not arbitrary.** The two halves have genuinely different needs: a widget
is small, glanceable, high-frequency and vehicle-bound; an applet is large, immersive, and
mostly wants a network rather than a CAN bus. Stremio does not care what the RPM is. Forcing
both through one extension model would over-serve one and under-serve the other.

**Keeping in-process for first-party** preserves ADR-0002's reasoning where it still applies,
and avoids paying an IPC tax on the components most likely to be written.

## Alternatives

- **Open the in-process model to third parties** — simplest, and what ADR-0002 already
  built. Rejected: it hands arbitrary code the user's privileges in a vehicle, and the
  containment ADR-0002 relies on (wrap, time-box, auto-disable) addresses *bugs*, not
  *intent*. Publishing changes which of those matters.
- **Out-of-process native plugins over IPC** — real isolation, language-agnostic. Rejected
  for the reason ADR-0002 gave and which still holds: it taxes every component with IPC
  boilerplate and makes UI embedding hard. Worth revisiting only if applets prove too
  limited for something people genuinely want.
- **Widgets as code too** — consistent, but reintroduces the trust question precisely where
  it buys least. Rejected.
- **Stay private; keep the SDK as internal discipline.** Entirely legitimate — the outline's
  original position, and "the SDK is built as if for others because that discipline produces
  a better architecture" already captures most of the benefit. Rejected because publishing
  is now the intent, but it remains the cheapest answer if the maintenance cost bites.

## Consequences

- **The outline's non-goal is now wrong.** It lists "public distribution or a component
  marketplace" among non-goals, with "the audience is one truck". That must be amended.
- **There are now three contracts to version, not one.** `DashDeck.Abstractions` for
  first-party components, the widget JSON schema, and eventually the applet bridge. Each
  needs its own compatibility story; conflating them would undo ADR-0008.
- **Widgets cannot do anything a signal cannot express.** No history charts, no custom
  drawing, no derived values. Accepted deliberately: that is what first-party components and
  applets are for, and a widget that needs more than a number is asking to be one of those.
- **Publishing brings responsibilities the project has not carried before** — a security
  posture, an issue tracker, versioning discipline, authoring documentation. These are real
  and recurring, and are the strongest argument for the rejected "stay private" option.
- **A sandbox is not a guarantee about behaviour.** An applet cannot read the filesystem,
  but it can still use the network. Nothing here constrains what a page does with data it
  was legitimately given — and once the bridge exists, that includes vehicle signals.
- Widget validation becomes a real surface: an unknown signal id, an out-of-range rate or a
  bad size must fail loudly at load rather than half-working (ADR-0002's rule for manifests,
  extended).
