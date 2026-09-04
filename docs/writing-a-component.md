# Writing a DashDeck component

This is the practical guide to building a component — a small piece of code that adds a
capability to the dash. If you want the *why* behind the design, read
[ADR-0023](decisions/ADR-0023-component-host.md) and
[ADR-0002](decisions/ADR-0002-plugin-model.md); this is the *how*.

A component is the unit the whole project exists to make cheap. Adding one should mean one
small project and a folder — not a change to the app.

---

## What a component is

A folder under `plugins/`, holding a manifest and a DLL:

```
plugins/
└─ com.jpenner.tripcomputer/
   ├─ component.json      ← the manifest: who you are, what you need
   └─ TripComputer.dll    ← your code
```

Drop the folder in, restart DashDeck, and it appears. That is the whole install story. There
is no registry, no installer, no central list to edit.

Your code implements one interface — `IDashComponent` — and, if it draws something,
a second one, `IDashComponentView`. Both live in `DashDeck.Abstractions`, the one small,
stable assembly you reference. You never touch the vehicle, the adapter, or CAN directly; you
ask for **named signals** and the host does the rest.

---

## The five-minute version

Here is a complete component. It reads the vehicle's speed and remembers the fastest it has
seen this session.

**`TopSpeed.cs`**

```csharp
using System.Globalization;
using DashDeck.Abstractions;

namespace TopSpeed;

public sealed class Component : IDashComponent
{
    private IComponentContext _context = null!;
    private IDisposable? _subscription;
    private double _topKmh;

    public string Id => "com.example.topspeed";

    public Task InitializeAsync(IComponentContext context, CancellationToken ct)
    {
        _context = context;
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _subscription = _context.Signals.Subscribe("vehicle.speed", reading =>
        {
            if (reading.IsUsable && reading.Value > _topKmh)
            {
                _topKmh = reading.Value;
                _context.Logger.Log(LogLevel.Info, $"new top speed {_topKmh:0} km/h");
            }
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public Task SuspendAsync(CancellationToken ct) => StopAsync(ct);
    public Task ResumeAsync(CancellationToken ct) => StartAsync(ct);
}
```

**`component.json`**

```jsonc
{
  "id": "com.example.topspeed",
  "name": "Top Speed",
  "version": "0.1.0",
  "author": "You",
  "apiVersion": "1.0",

  "entry": { "assembly": "TopSpeed.dll", "type": "TopSpeed.Component" },
  "surfaces": ["backgroundWorker"]
}
```

That is a real, working component. It has no face yet — it just watches and logs — but it
loads, runs, and is contained if it misbehaves. Adding a widget is the next section.

---

## The manifest

`component.json` is read and checked **before any of your code runs**. If it is malformed, or
asks for something the host cannot give, your component is rejected with a message and its DLL
is never even loaded. So the manifest is where a mistake is cheapest.

| Field | Meaning |
|-------|---------|
| `id` | Your component's identity. Reverse-DNS by convention (`com.you.thing`) so two authors never collide. **Must match** the `Id` your code returns. |
| `name` | Human name, shown in diagnostics. |
| `version` | Your component's own version. Yours to manage; the host does not interpret it. |
| `author` | You. |
| `apiVersion` | The contract version you built against — `"1.0"` today. The host serves a range and checks this; see [Versioning](#versioning). |
| `entry.assembly` | Your DLL's filename, beside the manifest. |
| `entry.type` | The fully-qualified name of your `IDashComponent` class. |
| `surfaces` | What you offer: `widget`, `fullScreen`, `statusItem`, `backgroundWorker`. |
| `widget` | Sizing hints for your widget: `{ "preferredSize": "1x1", "minSize": "1x1" }`. Sizes are `columns x rows`; a card is one row tall and one or two columns wide. |
| `signals` | The vehicle signals you need, declared up front: `[{ "id": "vehicle.speed", "priority": "normal", "rateHz": 2 }]`. Every id must exist in the catalog or the component is rejected. |
| `permissions` | What you are asking for, e.g. `["storage", "signals.read"]`. `vehicle.actions` is never granted (the dash is read-only until Phase 3). |

Unknown fields are ignored, so a manifest written against a newer SDK still loads.

---

## The lifecycle

Your `IDashComponent` gets five calls, and the host makes every one of them behind a wall: if
you throw or hang, you are contained, not trusted — after a few faults the host stops calling
you and your card shows a "stopped" state. So none of these need defensive plumbing against
the *host*; they need to be honest about *your* work.

- **`InitializeAsync(context, ct)`** — called once, first. You are handed your
  [context](#the-context). Do cheap wiring here and hold the context; there is no second
  delivery. Anything expensive waits for `StartAsync`, because you are initialised whether or
  not you are ever shown.

- **`StartAsync(ct)`** — you became visible. Subscribe to signals, start timers, acquire what
  only a shown component needs.

- **`StopAsync(ct)`** — you went off the visible page. **Release your signal declarations
  here.** This is not optional tidiness: the whole app shares one modest budget on one
  serialised link, and a component that keeps declaring signals nobody can see is spending that
  budget on nothing (see [the rules](#the-rules)). You may be started again without warning.

- **`SuspendAsync(ct)` / `ResumeAsync(ct)`** — the app was backgrounded or the vehicle link
  dropped, then came back. Heavier than stop/start; expect no data in between. For most
  components, suspend is just stop and resume is just start.

> **Assume you will be stopped.** A dash is closed by having its power pulled, so `StopAsync`
> may never be reached. Persist anything you care about as you go, through `Storage` — not in a
> shutdown handler.

---

## The context

`InitializeAsync` hands you an `IComponentContext`. It is the only way you reach anything
outside your own code, which is what lets the host know — and bound — what you can do.

```csharp
public interface IComponentContext
{
    IVehicleSignals    Signals  { get; }  // named signals, never PIDs
    IComponentStorage  Storage  { get; }  // your own private key/value store
    IComponentSettings Settings { get; }  // your settings, as the user configured them
    IComponentLogger   Logger   { get; }  // diagnostics, tagged with your id
    IClock             Clock    { get; }  // the injected clock — never DateTime.Now
    IVehicleActions?   Actions  { get; }  // null, always, until Phase 3
}
```

**`Signals`** is how you read the vehicle. Two methods matter:

- `Subscribe(id, onValue)` — observe every new reading. Returns an `IDisposable`; dispose it in
  `StopAsync`.
- `Require(id, priority, rateHz)` — declare that you *need* a signal at roughly a rate, which
  the arbiter schedules. Also returns something to dispose in `StopAsync`. Requiring creates
  demand; subscribing only watches what is already flowing. If you need a signal to be polled
  because of you, `Require` it.

Every reading is a `SignalValue` carrying `Value`, `Unit`, `TimestampUtc`, and a `Quality`
flag. **Check the quality.** `reading.IsUsable` is true only for `Live` or `Simulated`
readings; a `Stale` or `Unavailable` value is one you should show as such, not compute with.

**`Clock`** is injected on purpose. A component that reads `DateTime.Now` cannot be replayed
against a recorded drive or tested against a scripted one. Time is data here — read
`context.Clock.UtcNow`.

**`Actions`** is `null` and will stay `null` until Phase 3. The dash does not write to the
vehicle, and the absence of that power is a null reference rather than a promise you could
forget — there is simply no method to call.

---

## Drawing a widget

To put something on the dash, also implement `IDashComponentView`:

```csharp
public interface IDashComponentView
{
    FrameworkElement CreateWidget();               // required
    FrameworkElement? CreateFullScreen() => null;  // optional, opened when tapped
}
```

This lives in `DashDeck.Abstractions.Wpf`, so a component with a face references that assembly
and targets `net10.0-windows`. A headless component (a logger, a bridge) references only
`DashDeck.Abstractions` and builds anywhere.

Three things make a widget look like it belongs, rather than like a foreign rectangle dropped
on the dash:

**1. Return content, not a card.** The host draws the card frame — the surface, the border, the
rounded corners, the padding — around every component widget, the same frame every signal card
wears. You return only what goes *inside*. Do not draw your own background or border; you will
end up with a card inside a card.

**2. Use the host's brushes and fonts.** Read them by key so your widget follows the dash's
theme, including day and night, instead of freezing at one set of colours:

```csharp
static Brush Brush(string key, Color fallback) =>
    Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

static FontFamily Font(string key) =>
    Application.Current?.TryFindResource(key) as FontFamily ?? new FontFamily("Segoe UI");
```

The palette a card uses:

| Key | For |
|-----|-----|
| `TextHighBrush` | the value — the thing being read at a glance |
| `TextMidBrush` | the unit beside it |
| `TextLowBrush` | the caption / label |
| `SurfaceBrush`, `HairlineBrush` | the card frame (the host uses these; you don't need them) |
| `UiFont` | the value's font |
| `MonoFont` | captions, units, small type |

Always pass a fallback, so your widget still renders if it is ever hosted somewhere that has
not defined those keys.

**3. Update on the UI thread.** Signal callbacks arrive on the vehicle worker thread. The
simplest safe pattern is not to push from the callback into your controls at all, but to keep
the latest value in a field and let a `DispatcherTimer` on the widget read it four times a
second:

```csharp
var timer = new DispatcherTimer(DispatcherPriority.Background)
{
    Interval = TimeSpan.FromMilliseconds(250),
};
timer.Tick += (_, _) => valueText.Text = _latest.ToString("0.0");
timer.Start();
element.Unloaded += (_, _) => timer.Stop();   // don't tick against a control nobody can see
```

Four times a second is the sweet spot: a dash number that updates slower reads as frozen.

Look at [`components/TripComputer/TripComputer.cs`](../components/TripComputer/TripComputer.cs)
for a worked example of all three.

---

## Building and deploying

Your project references the contract and, if you draw, its WPF half. Mark both **not private**
so no copy of them is emitted beside your DLL — the host owns the one copy every component
shares, and a second copy is exactly what the loader has to ignore:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>  <!-- net10.0 if headless -->
    <UseWPF>true</UseWPF>                                <!-- omit if headless -->
    <Nullable>enable</Nullable>
    <AssemblyName>TopSpeed</AssemblyName>
    <PluginDir>$(MSBuildProjectDirectory)\..\..\plugins\com.example.topspeed</PluginDir>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\DashDeck.Abstractions\DashDeck.Abstractions.csproj">
      <Private>false</Private>
    </ProjectReference>
    <!-- only if you draw a widget: -->
    <ProjectReference Include="..\..\src\DashDeck.Abstractions.Wpf\DashDeck.Abstractions.Wpf.csproj">
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>

  <!-- Drop the DLL and manifest into plugins/<id>/ on every build. -->
  <Target Name="DeployToPlugin" AfterTargets="Build">
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(PluginDir)" />
    <Copy SourceFiles="component.json" DestinationFolder="$(PluginDir)" />
  </Target>
</Project>
```

Keep `component.json` beside your source (tracked), and let the build copy it into `plugins/`
alongside the DLL. `plugins/` itself is a build output — a fresh checkout regenerates it — so
nothing hand-edited lives there.

`publish.ps1` builds every component under `components/` and ships `plugins/` beside the
executable, so a deployed tablet build finds your component the same way the dev build does.

---

## Verifying it loaded

The shell has a flag that loads `plugins/`, reports what happened, and exits — the fastest way
to see your component is found and healthy before it has a face:

```bash
dotnet run --project src/DashDeck.Host -- --components report.txt
```

`report.txt` lists each component with its state — `Active`, `Stopped`, `Incompatible`,
`Rejected` — and the reason if it did not load. Add `--components-dwell 6` to let a background
worker run for six seconds first, so you can see it actually working (and check its storage
afterwards).

Then, to see a widget on the dash, add a card that points at your component. In
`dashboard.json` (or the shipped default), a component card looks like:

```jsonc
{ "id": "top", "signal": "com.example.topspeed", "source": "Component" }
```

The `signal` field names your component id, and `source: "Component"` tells the dash it is not
a catalog signal. A card naming a component that is not installed is kept and shown as
`UNAVAILABLE` rather than vanishing.

---

## The rules

These are not style preferences. Each one is something that goes wrong in a moving vehicle if
you ignore it.

1. **Subscribe to signals, never to hardware.** If you find yourself wanting a PID, the signal
   catalog is missing a definition — it is added there, in config, not guessed at in code. A
   wrong value is in range, so nothing downstream can catch it.

2. **Declare your rate honestly, and drop it when hidden.** You share one modest budget on one
   serialised link with the whole app. Asking for 30 Hz does not get you 30 Hz; it degrades
   everyone. And release your declarations in `StopAsync` — a component nobody can see should
   ask for nothing.

3. **Render staleness.** Every value carries a quality flag. Show `Stale`, `Unavailable` and
   `Simulated` states honestly. A confidently wrong number on a windscreen is worse than a
   blank one.

4. **Never block the UI thread.** The dash must not freeze while someone is driving. Everything
   vehicle-related is already asynchronous; keep it that way, and keep widget updates cheap.

5. **Assume you will be stopped.** Persist as you go. `StopAsync` may not be reached.

6. **Design for a glance.** Large type, high contrast, few targets. If it needs reading, it
   does not belong on a screen used at speed.

---

## Versioning

`DashDeck.Abstractions` carries a contract version — `ComponentApi.Version`, `"1.0"` today.
Your manifest declares the version you built against, and the host serves a **range**: same
major, minor no higher than it understands. So a component built against `1.0` keeps loading as
the host advances. A breaking change to the contract bumps the major and is a deliberate,
documented event — which is exactly why the first components are built through this contract
now, to exercise it before anyone depends on it.

---

## When it does not load

`--components` will tell you which of these happened:

| State / reason | What it means |
|----------------|---------------|
| `Rejected: Manifest ... has no id` (and similar) | The manifest is missing a required field. |
| `Rejected: ... declares unknown signal 'x'` | A signal id in your manifest is not in the catalog. Fix the id, or the catalog is missing that signal. |
| `Rejected: ... entry assembly 'X.dll' is missing` | The DLL named in `entry.assembly` is not beside the manifest. Did the build copy it? |
| `Rejected: ... entry type 'X.C' was not found` | `entry.type` does not match your class's full name. |
| `Rejected: ... is not an IDashComponent` | Your class does not implement `IDashComponent`. |
| `Rejected: ... code reports a different id` | Your class's `Id` does not match the manifest `id`. They must agree. |
| `Incompatible: apiVersion X is not served` | You built against a contract version this host does not serve. |
| `Disabled` | Your component loaded but faulted repeatedly and the host gave up on it for the session. Check the log for the fault. |

---

That is the whole surface. A component is a small class, a manifest, and a folder — and if it
misbehaves, it is contained rather than fatal. Start from the five-minute version, get it
loading with `--components`, then give it a face.
