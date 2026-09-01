# ADR-0003 — Transport and adapter are separate layers

**Status:** Accepted · 2026-09-01

## Context

The stated preference is a Bluetooth OBD-II adapter, with a possible move to USB via a
Surface dock later, and no adapter purchased yet. A raw CAN interface is a plausible
future if throughput ever becomes the binding constraint.

## Decision

Two layers with separate contracts. `IVehicleTransport` moves bytes and knows no
protocol. `IVehicleAdapter` speaks a command protocol and knows no pipe.

## Reasoning

Bluetooth and USB to an OBDLink-class adapter are *the same protocol over different
pipes*. Collapsing them into one layer would mean writing the ELM/STN protocol twice, or
writing it once and then untangling it later under pressure.

Splitting also makes the synthetic vehicle and log replay ordinary transports rather than
special cases — which is what lets the entire stack be built with no hardware (ADR-0005).

## Consequences

- Leaning Bluetooth now costs nothing if a dock is added later: one new transport.
- A raw CAN interface later is a new adapter reporting higher `AdapterCapabilities`.
  Nothing above it changes — which is the whole escape hatch for risk R1.
- One extra interface hop on every byte. Irrelevant at 10–20 requests per second.
