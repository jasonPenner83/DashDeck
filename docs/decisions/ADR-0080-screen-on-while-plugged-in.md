# ADR-0080 — The screen stays on while the tablet is plugged in

**Status:** Proposed · 2026-10-28
**Builds on:** [ADR-0068](ADR-0068-screen-brightness.md) (the dash looks after the screen, and gives back what it borrows).
**Keeps:** C1 (nothing installed, no system setting changed).

## Context

The owner asked: "if the DashDeck is running and device is charging keep the screen on". In the
truck the tablet runs from the truck's charger. Windows' display timeout still applies, so the dash
went dark, and then locked, partway through a drive.

## Decision

1. **While DashDeck runs and the tablet is plugged in, the screen stays on.**
   - "Plugged in" is `GetSystemPowerStatus`'s AC line being online. That covers charging, and also a
     full battery that has stopped charging but is still on the charger. The screen going dark when
     the battery reaches 100 % would be the bug again.
   - On battery, or when Windows does not say, nothing is asked, and Windows' own sleep and lock
     settings apply. Once the truck stops powering the charger, the tablet behaves as it
     always did.
2. **A request, not a setting.** `SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED |
   ES_SYSTEM_REQUIRED)` asks Windows to keep the display and the system awake for as long as the
   request stands. `ES_CONTINUOUS` alone withdraws it. No power plan, timeout or registry value is
   touched, and Windows drops the request by itself when the process ends, even after a crash.
3. **On the shell's one-second beat**, on the dispatcher: the request belongs to the thread that
   makes it, so it is always made on that thread. Windows is asked only when the answer changes,
   and once more on the way out.
4. **Nothing to set.** Settings ▸ DISPLAY ▸ SCREEN shows one line: *Kept on: the tablet is plugged
   in*, *On battery: …*, or *Windows would not keep the screen on* when the request was refused.
5. The rule is pure in Core (`ScreenAwake`); `Brightness.ScreenKeptOn` in the dash reads the power
   and makes the request.

## Consequences

- With the engine running the dash stays lit and unlocked, at the backlight level ADR-0068 sets.
- At a desk on the charger the screen stays on while DashDeck is open too. Closing DashDeck gives the
  timeout back.
- The tablet does not sleep while plugged in with DashDeck open, so it does not lock either.

## Not done

- A switch to turn it off. The owner asked for it always; one can be added in DISPLAY if wanted.
- Keeping the screen on on battery.
