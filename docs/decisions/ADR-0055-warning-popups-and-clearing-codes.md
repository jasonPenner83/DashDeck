# ADR-0055 — Warning popups, trouble codes, and clearing them: the first gated write

**Status:** Accepted · 2026-10-05
**Builds on:** [ADR-0041](ADR-0041-console-dash.md) (the console's warning lights),
[ADR-0053](ADR-0053-catalog-driven-simulator.md) (placeholders). **First use of:**
[ADR-0006](ADR-0006-additive-read-only.md)'s gated `IVehicleActions` choke point — for one action.

## Context

The console lights its warnings (ADR-0041), but a light on DASH is easy to miss while CARDS, a map
or a video has the screen. The owner asked for a window that pops up when a warning light comes on,
with a way to dismiss it, and to clear the codes if possible. Asked how, he chose:

- **Clear codes: build it, fully gated** — a master switch off by default, only when parked with the
  engine off and the ignition on, a hold to confirm that shows the codes and what clearing costs, an
  audit log, and the codes read and shown first.
- **Which lights:** the serious ones pop up — check engine, oil pressure, hot coolant, charging, brake;
  low fuel, door, seatbelt and tyres stay lights only. Each is a switch.
- **While moving:** a strip with the light, one word and a big DISMISS; **stopped:** the full window.
- **Dismiss:** stays dismissed while the light stays on; pops again when it clears and comes back, or
  when a new code is stored.

Clearing codes is mode 04: it is a **write**. It turns the light off and erases the stored and pending
codes, the freeze frame and the readiness monitors. It is not configuration and not reflashing — it
is what every scan tool does and what a battery disconnect does — but ADR-0006 says no write ships
without all five gates.

## Decision

### Warnings are a file

`catalog/warnings.json` lists each warning: its signal and the same conditions the console uses
(`bit`, `below`, `equals`, `onAt`), a title, a one-word reason, an icon, `severity` (`stop` red or
`caution` amber — fixed colours, not theme tokens, like the quality colours), `popup` default,
`holdSeconds`, `whenMoving` (brake: a parking brake set while parked is no news), `countSignal` (check
engine's stored-code count) and `codes` (the window reads codes).

### The monitor

`WarningMonitor` (Core) is fed on the shell's one-second beat with what the bus holds; it asks nothing
itself.
- A light is lit after reading lit for its hold time, and off after reading off as long.
- **Only a Live or Simulated reading is evidence.** Stale or Unavailable — ignition off, adapter lost,
  a placeholder on the real truck — changes nothing, so a light is never claimed off for want of an
  answer, and the placeholders (oil, brake, door, seatbelt, tyres) never fire on the real truck.
- Dismissed stays dismissed until the light is seen off and comes back, or its count rises. Dismissals
  are kept in `settings.json` (`dismissedWarnings`), so a light that has been on for a week does not
  pop at every start.
- Demand is declared like a card's: the lights whose popup is on at 0.25 Hz, and the speed at 0.5 Hz
  (Low priority). The default set costs about 1.5 requests a second, and less when the console is
  already asking.

### On screen

- **Moving** (above 5 km/h): a strip over the status strip — icon, one word, DISMISS. It sits in the
  strip's row, not over the stage, because an occupant's child window draws over WPF content there.
- **Stopped**: a full-screen window — title, advice, the codes, DISMISS. The stage occupant is
  collapsed beneath it and keeps running, like under the picker.
- **Settings ▸ Diagnostics**: every warning with its state (LIT, OFF, NO READING, LIT · DISMISSED)
  and a POPUP switch; the codes with READ CODES; ALLOW CLEARING CODES; the log's path.

### Reading codes

The trouble-code services take no PID: `PidRequest.Service(mode, bus, header)` sends `03`, `07`, `04`;
the parser expects no PID echo, and `04`'s reply is `44` alone. `TroubleCodeReader` asks each
emissions module **by address** (`7E0`–`7E7` named in the reference or the user's vehicle file),
because the parser keeps only the first broadcast answer. With none named, it asks the broadcast.
Descriptions come from `catalog/reference/dtc-descriptions.json`: SAE J2012's groups and a few common
standard codes in our words. A maker's own code is described as one, never guessed at.

### Clearing: the five gates

`VehicleActions` (Core) is the choke point. `ClearDiagnosticCodesAsync` sends mode 04 to the broadcast
on HS only when:

1. **Permission** — the action is the shell's alone. Components still see `IComponentContext.Actions`
   as null and no manifest permission names a write. `DashDeck.Abstractions` is unchanged, so
   `apiVersion` is too.
2. **Master switch** — `allowClearCodes` in settings, **off by default**, read at the moment of
   clearing.
3. **Interlock** — read fresh from the truck at that moment through `SignalProbe`: speed under 0.5 km/h
   and rpm under 50, both answered (no answer means the ignition is off: refused).
4. **Confirm** — a hold of two seconds (`HoldButton`, touch handled directly), used once, within ten
   seconds. The choke point checks the hold itself rather than trusting the button.
5. **Audit log** — `%LOCALAPPDATA%\DashDeck\actions.log`, one line per attempt: refusals with why,
   and SENDING with the interlock readings and the codes the screen showed, then the outcome. **If the
   SENDING line cannot be written, nothing is sent.**

After a clear the codes are read again, so the screen shows the result rather than claiming it.

### Desk testing

The synthetic truck takes faults (`SimulatedFaults`): `--fault P0420`, `--fault P0171/pending`,
`--fault warning.oilPressure`, `--fault engine.coolantTemp=118`, `--fault engine.rpm=0` (engine off,
so clearing can be tried), each optionally `@from` or `@from-to` in seconds. Modes 03, 07 and 04 are
answered by the engine computer from them; 7E1 has none.

## Consequences

- Phase 3 starts with one action, the least dangerous write there is. The choke point, the confirm
  control and the log are built once; a later action adds a method and its own interlock, and still
  passes all five gates. Tailgate and wheel heat (the owner's other wishes) are **not** this: they are
  body-module commands still to be found, and each will need its own ADR.
- Clearing resets the readiness monitors, so an emissions inspection fails until they have run again.
  The window says so every time it offers to clear.
- The real truck's popups today: check engine, hot coolant and low voltage (real signals). Oil
  pressure and brake pop only once their identifiers are found.
- Whether Ford's engine computer accepts mode 04 with the engine off and the ignition on is a standard
  behaviour, unmeasured in the cab (Q23).
