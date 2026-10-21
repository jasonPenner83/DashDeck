# ADR-0072 — Typing a destination while moving is the owner's switch

**Status:** Accepted · 2026-10-21
**Amends:** [ADR-0065](ADR-0065-address-search.md)'s decision 8 ("a destination cannot be typed
above 5 km/h") and the matching line of [ADR-0058](ADR-0058-offline-map-and-routing.md)'s
consequences — from a rule to a default. **Follows:** [ADR-0069](ADR-0069-match-a-number-you-name.md),
which took the same view of the ID hunter.

## Context

The map's search refuses to be typed into above 5 km/h. ADR-0065 called it a gate rather than a
warning: the field is disabled, and no request leaves while the truck is moving.

Jason asked for it to be bypassable, as a toggle in Settings. It is his truck and his dash; a
passenger can type, and so can he when he judges it is fine. The gate stops the dash from deciding
that for him, which is the same reasoning ADR-0069 applied to the hunter's moving checks.

## Decision

1. **Settings ▸ Display ▸ MAP SEARCH ▸ TYPE WHILE MOVING**, ON or OFF, saved as
   `searchWhileMoving` in `settings.json`.
2. **Off by default.** A new dash, or one whose settings file predates this, keeps the gate. Turning
   it on is a deliberate act by the person who owns the truck.
3. **On lifts the gate entirely**: the field is enabled and a search is sent at any speed. Nothing
   else about searching changes — Nominatim's one-a-second rule, the cache and the bias still hold.
4. **Read on the beat**, like the speed itself, so the switch takes effect without a restart and
   turning it off locks a moving search again at once.
5. **The refusal says where the switch is**, so the gate is never a dead end:
   *Stop first — … Settings ▸ Display ▸ TYPE WHILE MOVING lifts this.*
6. `SearchGate.MayType(speed, whileMoving)` in Core holds the rule and is tested; the search view-model
   takes the switch as a function, and the shell hands it the setting.

## Consequences

- The dash no longer guarantees that no destination is typed on the move; it guarantees that it is
  not typed on the move **unless the owner has said so**.
- ADR-0055's warning popups and ADR-0035's refusal of the dash's own sweeps while moving are
  unchanged: those protect the gauges and the truck, not attention.
