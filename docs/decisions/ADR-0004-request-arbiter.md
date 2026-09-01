# ADR-0004 — Components declare signals; an arbiter schedules them

**Status:** Accepted · 2026-09-01

## Context

An ELM327/STN-class adapter over Bluetooth serial is a text-protocol device with a
sustained ceiling around **10–20 requests per second in total** — shared by everything in
the app. Meanwhile the entire premise of the project is that components can be added
freely.

Those two facts are in direct conflict. Five components each polling what they need would
starve each other, and the symptom would be every component getting slower as the
ecosystem grows — the exact failure that makes people stop adding components.

## Decision

Components never request data. They **declare** the signals they need with a priority and
a desired rate. A Request Arbiter merges all live declarations into a single deduplicated
polling plan against the adapter.

## Reasoning

The scarce resource has to be scheduled centrally, because only the centre can see total
demand. Declaration also makes demand *inspectable*: the app can show you exactly what is
being polled and what it costs, and can drop invisible components to background priority
automatically.

Degradation is explicit rather than silent. When the plan exceeds the ceiling,
low-priority signals are shed first and subscribers are told their effective rate, so a
component can render "1 Hz" honestly instead of appearing frozen.

## Consequences

- Slightly more ceremony for component authors: declare up front, in the manifest or via
  `Require`.
- Components must handle getting a lower rate than they asked for. This is enforced by
  the synthetic vehicle simulating the same ceiling, so the failure mode appears during
  mock-data development rather than on the first drive.
- This is the load-bearing decision for the ecosystem thesis. If it is wrong, the third
  component is where it shows.
