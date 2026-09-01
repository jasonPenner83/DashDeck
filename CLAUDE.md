# CLAUDE.md — DashDeck

Working memory for this repo. Read this first; it is kept current deliberately.

## What this project is

A Windows infotainment app for Jason's **2019 Ford F-150**, running on a **Surface Pro 9
(Intel)** mounted in **portrait**, carried in and out of the truck. It reads vehicle data
over an OBD-II adapter and presents it through **pluggable components**.

The point of the project is the **ecosystem**, not any one feature. Adding a capability
should mean one small project against a stable SDK plus a folder in `plugins/`. When a
design choice trades ecosystem quality against a single feature's convenience, the
ecosystem wins.

Start with [`docs/00-project-outline.md`](docs/00-project-outline.md).

## Current state

**Pre-code.** The outline, architecture, hardware research and ADRs exist. No solution,
no source yet. Next step is the P0 skeleton.

## Things that are easy to get wrong here

1. **No hardware exists yet.** The adapter is *chosen* (OBDLink EX over wired USB,
   ADR-0007) but not bought. Everything runs on the synthetic vehicle (ADR-0005). Do not
   write code that assumes a truck is attached, and do not defer work waiting for hardware.
2. **The Surface is a personal device.** No kiosk mode, no shell replacement, no services,
   no registry writes. Self-contained folder deploy, settings in `%LOCALAPPDATA%`. One
   Microsoft-signed FTDI USB serial driver is the single accepted exception (ADR-0007).
   This rules out solutions that would otherwise be obvious.
3. **Components never touch CAN.** They subscribe to named signals. If a component needs
   a PID, the signal catalog is missing a definition — fix it there, in config.
4. **Nothing polls the adapter directly.** Components *declare* signals; the Request
   Arbiter builds one plan (ADR-0004). The old "~10–20 requests/second" figure was a
   *Bluetooth* limit and no longer applies now the link is USB — but **the real ceiling is
   unmeasured**, so do not assume headroom. The simulator keeps the conservative number
   until P1.5 measures a real one (Q12).
   Signals also declare a bus (`hs` / `ms`) and the arbiter interleaves across both.
5. **Read-only until Phase 3.** No writes to the vehicle. When they arrive they pass the
   five gates in ADR-0006 — all five, or it does not ship.
6. **The truck must work without us** (ADR-0006). SYNC 3 stays. Nothing here may degrade
   the vehicle when DashDeck is closed or absent.
7. **Signal quality is rendered, never hidden.** Every value carries `Live` / `Stale` /
   `Unavailable` / `Simulated`. A confidently wrong number on a dash is worse than a blank.
8. **`IClock` is injected.** Never `DateTime.Now` — it breaks replay and scripted-drive
   tests.

## Conventions

- **Stack:** .NET 9, C#, WPF (ADR-0001). Custom design system over the .NET 9 Fluent theme.
- **Portrait-first.** Primary navigation lives in the bottom third — the only band
  reachable from the driver's seat.
- **Layering is strict.** Transport → adapter → catalog → arbiter → state bus → services →
  component host → shell. No layer reaches past its neighbour.
- **`DashDeck.Abstractions` stays small, stable and dependency-free.** It is the only
  assembly components reference, and it is shared across every load context.
- **The signal catalog is data.** New signals are JSON, never a recompile.
- Never block the UI thread. All vehicle I/O is serialised on its own worker.

## Decisions

ADRs live in [`docs/decisions/`](docs/decisions/) and are immutable once accepted — a
changed decision gets a new ADR that supersedes the old one. Six exist so far, covering
the UI stack, plugin model, transport split, request arbiter, mock-first development, and
the additive/read-only posture. **Read them before proposing an architectural change**;
several rejected alternatives were rejected for reasons that are not obvious from the
code.

## Working agreement

- Interview rather than assume. The scoping for this project was done by asking; keep
  doing that when a choice would change the shape of the work.
- Open questions go in [`docs/04-open-questions.md`](docs/04-open-questions.md) rather
  than being silently resolved.
- When a decision gets made, write the ADR in the same change that implements it.
