# ADR-0005 — The synthetic vehicle is the primary data source until hardware exists

**Status:** Accepted · 2026-09-01

## Context

No OBD-II adapter has been purchased. Separately, the Surface Pro 9 is a personal device
that is only sometimes in the truck, so even after hardware arrives the app will usually
run with no vehicle attached.

## Decision

Build a **synthetic 2019 F-150** as a transport, and treat it as the primary data source
for Phases P0 and P1. Hardware bring-up becomes its own milestone (P1.5) rather than a
prerequisite for anything.

## Reasoning

Waiting on hardware would block all work for no gain, and building against mock data is
not a compromise here — it is better in one specific way that matters: a scripted drive
with a *known* fuel consumption gives an exact expected value to assert against.
A real drive never can. The trip computer's maths can be tested properly precisely
because the data is synthetic.

The risk of mock-first is building against a simulator that is too well-behaved and
discovering every real-world failure at once on the first drive. So the synthetic vehicle
is required to misbehave realistically:

- A cold start that warms up over minutes.
- Fuel economy that responds to speed and throttle.
- Dropped and late responses.
- **The same ~10–20 requests/second ceiling as a real Bluetooth ELM adapter** (ADR-0004).

## Consequences

- Every layer above the transport is complete and tested before an adapter is bought.
- Signal quality carries a `Simulated` flag that components must render, so mock data can
  never be mistaken for real data on screen.
- Scripted drives survive after bring-up as regression fixtures.
- The measure of success for P1.5: the trip computer runs on real CAN data **unmodified**.
  If it needs changes, the abstraction was wrong and this ADR was over-optimistic.
