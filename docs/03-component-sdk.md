# DashDeck — Component SDK

The ecosystem contract. This is the document a component author reads, and the reason
the rest of the architecture is shaped the way it is.

## What a component is

A folder under `plugins/<component-id>/`:

```
plugins/
└─ com.jpenner.tripcomputer/
   ├─ component.json          ← manifest
   ├─ TripComputer.dll        ← implements IDashComponent
   └─ icon.svg
```

Drop the folder in, restart the app, and it appears. That is the whole install story.

## The manifest

```jsonc
{
  "id": "com.jpenner.tripcomputer",
  "name": "Trip Computer",
  "version": "1.0.0",
  "author": "Jason Penner",
  "apiVersion": "1.0",              // checked against the host's supported range

  "entry": { "assembly": "TripComputer.dll", "type": "TripComputer.Component" },

  "surfaces": ["tile", "screen"],   // tile | screen | statusItem | backgroundWorker
  "tile": { "preferredSize": "2x1", "minSize": "1x1" },

  "signals": [                      // declared, not requested — see arbiter
    { "id": "vehicle.speed",   "priority": "high",   "rateHz": 4 },
    { "id": "engine.fuelRate", "priority": "normal", "rateHz": 2 }
  ],

  "permissions": ["storage", "signals.read"],   // "vehicle.actions" requires a grant
  "settings": { "$schema": "./settings.schema.json" }
}
```

Manifests are validated at load. A component that declares a signal the catalog does not
define, or an `apiVersion` outside the host's range, fails to load with a clear message
rather than half-working.

## The interface

```csharp
public interface IDashComponent
{
    string Id { get; }

    Task InitializeAsync(IComponentContext context, CancellationToken ct);

    // Called only for surfaces declared in the manifest.
    FrameworkElement CreateTile();
    FrameworkElement CreateScreen();

    Task StartAsync(CancellationToken ct);   // became visible / activated
    Task StopAsync(CancellationToken ct);    // hidden — release expensive resources
    Task SuspendAsync(CancellationToken ct); // app backgrounded or vehicle disconnected
    Task ResumeAsync(CancellationToken ct);
}
```

Lifecycle is tied to visibility, and that is enforced rather than advisory: a component
that is not on screen has its signal declarations dropped to background priority so it
stops consuming the adapter's limited budget.

## The context

```csharp
public interface IComponentContext
{
    IVehicleSignals    Signals { get; }   // subscribe by name; never touches CAN
    IComponentStorage  Storage { get; }   // isolated scope; cannot see other components
    IComponentSettings Settings { get; }
    INavigation        Navigation { get; }
    ILogger            Logger { get; }
    IClock             Clock { get; }     // injected — never DateTime.Now, so replay works
    IVehicleActions?   Actions { get; }   // null unless vehicle.actions was granted
}
```

Two details worth calling out:

**`IClock` is injected.** A component that reads `DateTime.Now` cannot be replayed or
tested against a scripted drive. Time is data here.

**`Actions` is nullable by design.** Absence of permission is expressed in the type
system, not in a runtime exception, so a component physically cannot call a write it was
not granted.

## Writing one

```bash
dotnet new dashdeck-component -n MyComponent
```

The template produces a working tile that subscribes to one signal, plus its manifest and
a test that runs it against a scripted drive. In the repo, `/component` (a repo-local
Claude skill) does the same thing conversationally.

## Rules for component authors

1. **Subscribe to signals, never to hardware.** If you find yourself wanting a PID, the
   signal catalog is missing a definition — add it there.
2. **Declare your rate honestly.** You share a ~10–20 requests/second budget with the
   whole app. Asking for 30 Hz does not get you 30 Hz; it degrades everyone.
3. **Render staleness.** Every value carries a quality flag. Show `Stale`, `Unavailable`
   and `Simulated` states. A confidently wrong number on a dash is worse than a blank.
4. **Never block the UI thread.** The dash must not freeze while someone is driving.
5. **Assume you will be stopped.** Persist anything you care about as you go; `StopAsync`
   may not be reached if the component is faulted.
6. **Design for a glance.** Large type, high contrast, few targets. If it needs reading,
   it does not belong on a screen used in a moving truck.

## Compatibility

`DashDeck.Abstractions` is semantically versioned and is the only assembly a component
references. The host supports a declared range of `apiVersion`s; breaking changes bump
the major and are recorded as an ADR. Since the component ecosystem is currently a
population of one, breaking changes are cheap now and expensive later — which is exactly
why the trip computer is built through the public SDK from day one, so the contract is
exercised before it ossifies.
