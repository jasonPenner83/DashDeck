# ADR-0008 — `main`/`develop` branching, and three independent version streams

**Status:** Accepted · 2026-09-01

## Context

The repository needed a branching model and a versioning scheme before code lands and the
choice gets made by default.

For a solo project, a `main`/`develop` split is usually ceremony that costs more than it
returns — a single branch with tags is normally the honest answer.

## Decision

Two long-lived branches, `main` and `develop`, plus **three independently versioned
artifacts**: the app (SemVer tags), the `DashDeck.Abstractions` contract (`apiVersion`),
and each component (its own SemVer).

## Reasoning

**Why the split is justified here despite the solo-project rule.** The artifact ends up in
a truck that gets driven. "Stable" is not an abstraction in this project — it is the build
running on the tablet at highway speed, and it is genuinely different from whatever is
mid-refactor. `main` means *this is what I drive with*. That distinction is worth a branch.

Everything beyond that is refused: no release branches, no hotfix branches, no GitFlow. A
bug found while driving is fixed on `develop`, merged and tagged like anything else.

**Why the version streams must not be welded together.** The ecosystem thesis (outline §1)
promises that a component written early still loads later. That promise is carried entirely
by `apiVersion`. If the SDK version tracked the app version, every app release would
nominally break every component and the promise would be void in practice even if nothing
actually broke. Host `v1.4.0` serving `apiVersion 1.0` must be normal.

**Why releases are zips on tags.** Constraint C1 already forces self-contained folder
deployment. That makes a GitHub Release asset the natural install channel, and gives
rollback for free: unzip the previous release. A rollback path needing no network, no
installer and no tooling is disproportionately valuable for software relied on in a
vehicle.

## Consequences

- Nothing is committed directly to `main`. Every merge into it is tagged.
- The app stays `0.x` through P0–P2. `1.0.0` is earned when breaking the SDK would be a
  real cost, not when the app feels finished.
- A major `apiVersion` bump requires its own ADR, because it invalidates every existing
  component.
- The host must validate `apiVersion` at load and fail clearly, not partially work.
- CI gates pull requests into both branches once code exists.
