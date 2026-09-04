# ADR-0024 — User-added stage apps, from the UI, persisted

**Status:** Accepted · 2026-09-03
**Relates to:** [ADR-0021](ADR-0021-owned-not-reparented.md) and
[ADR-0020](ADR-0020-native-app-occupants.md) (the `AppStageOccupant` this builds on), and
[ADR-0012](ADR-0012-widgets-and-applets.md) (web applets are the *third-party* model — still
unchanged). Persistence follows [ADR-0014](ADR-0014-custom-accents.md)'s `%LOCALAPPDATA%` rule.

## Context

`AppStageOccupant` already hosts any native app described by an `AppLaunchSpec` — launch, wait
for a window, own-and-place it over the stage, clamp it to the stage's size, and report
`Hosted`/`Outside` honestly. The only thing hardcoded was the **list**: Nuvio, Stremio and the
Probe are static `AppLaunchSpec` properties, each with a matching entry in `StageOption.All`.
Adding the next app — Chrome — meant editing two files, recompiling and redeploying.

Jason asked to add apps himself, through the UI: point at an installed `.exe`, name it, and
have it appear as a stage option. That is not new launching machinery; it is a persisted list
feeding the machinery that already exists.

## Decision

**A user can add native app launchers from Settings.** A new **APPS** section takes a name, an
executable chosen with a file dialog, and optional command-line arguments, and lists what has
been added with a remove control.

**The list is persisted** to `apps.json` in `%LOCALAPPDATA%\DashDeck\`, through the same
never-throwing `JsonFile` as every other store, with the same forward/back-compatible shape.

**A user entry becomes an `AppLaunchSpec` via `AppLaunchSpec.FromUser`** — a single concrete
candidate path rather than the built-ins' list of likely install locations — and flows through
the *same* `AppStageOccupant`. A user app is not a new kind of occupant.

**Built-ins stay in code.** Nuvio, Stremio and the Probe carry curated multi-candidate install
paths and hand-written detail, and are not migrated into the store or made removable. The user
store is purely additive, appended after the built-ins in the launcher.

**It appears live.** The store raises an event on edit; the shell rebuilds its stage options
from it without restarting whatever is on the stage.

**This is still a launcher, not an extension mechanism.** ADR-0012's answer for third-party
code is unchanged: web applets, sandboxed by the browser. Nothing here ships or sandboxes code;
it points the stage at software the user already installed on their own device.

## Reasoning

**The generic occupant is the whole reason this is small.** ADR-0020/0021 did the hard part —
adoption, ownership, the size clamp, the honest fallback states. Making the list user-editable
is a store, a factory method, and a settings pane; the risky code is untouched and already
proven on Stremio and Nuvio.

**A UI beats a code edit for a list that is inherently personal.** Which apps belong on *this*
tablet is Jason's call and changes over time. Encoding each one as a static property made every
addition a developer task for something that is really a preference, like the accent colour.

**The built-ins earn their place in code.** Their value is the install-path intelligence — that
Stremio 5 lives in `stremio-shell-ng.exe` inside the service folder, that Nuvio's MSI lands in
several places. A UI-added app has none of that to guess: the user browsed to the exact file.

**Live refresh is worth an instance store and an event.** The other stores are static because
one writer sets a value and something reads it later. Here the picker is built *from* the list
and must change when the list does, so the store is an instance that announces its edits and the
shell listens — the same shape the display settings already use for live web-scale changes.

## Alternatives

- **Hardcode Chrome like the others.** Rejected: it answers today and not the next app, and the
  next app is always coming. Jason explicitly asked to do it himself through the UI.
- **Restart to pick up a new app.** Simpler — no event, no rebuild. Rejected: adding a thing and
  watching for it to appear is the whole loop, and a restart in a mounted tablet is friction for
  no gain.
- **Migrate the built-ins into the same store and ship a seed file.** Rejected: it throws away
  the multi-candidate path intelligence, and a seed file that `publish.ps1` rewrites every build
  is a worse home for it than code.
- **A general plugin/registration API for native code.** Rejected hard: that is exactly what
  ADR-0012 declined. Web applets remain the third-party path; this launches the user's own exes.

## Consequences

- **DashDeck now launches whatever exe the user points it at**, with the user's privileges and
  no sandbox — the same capability ADR-0020 already introduced, now reachable from the UI rather
  than only from code. Acceptable for the same reason and no other: it is the user's own machine
  and their own installed software. It is not, and must not become, a third-party extension point.
- **A user app's name cannot shadow a built-in.** The editor refuses a name that collides with a
  stage option (built-in or already-added), so the picker stays unambiguous.
- **A launcher can go stale.** If the exe is later moved or uninstalled, the entry remains and
  reads `NotInstalled` when chosen — the same honest state a built-in shows, and the settings
  list carries the path so it can be fixed by removing and re-adding.
- **`apps.json` joins `settings.json`, `dashboard.json` and `mount.json`** in `%LOCALAPPDATA%`.
  Still C1-clean: uninstalling remains deleting a folder, and nothing touches the registry.
- **Verification of a launched app still needs a person** (ADR-0020): an owned window does not
  appear in a `--shot`. The settings pane, the store round-trip and the option-list merge are
  testable without one; the hosting itself is confirmed by eye, and the Probe/charmap remains
  the way to prove adoption without depending on a particular install.
