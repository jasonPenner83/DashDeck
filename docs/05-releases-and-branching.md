# DashDeck — Branching, Versioning and Releases

## Branches

| Branch | Means | Rule |
|---|---|---|
| `main` | **The build that is on the truck tablet.** | Never commit directly. Only merges from `develop`, and only when you would be happy driving with it. Every merge is tagged. |
| `develop` | Integration branch. Day-to-day work lands here. | May be broken between commits. Nothing here is expected to be drivable. |
| `feature/*`, `fix/*` | Where contributors work. | Branch from `develop`, open a PR back into `develop`. Delete after merge. |

That is the whole model. No release branches, no hotfix branches, no GitFlow. Ceremony
beyond this costs more than it returns. A bug found while driving is fixed on `develop`,
merged, and tagged as a patch like anything else.

Both `main` and `develop` are **protected**: changes arrive by pull request with CI green.
Contributors target `develop`; only the maintainer merges `develop` into `main`, because
only the maintainer drives the result. See [CONTRIBUTING.md](../CONTRIBUTING.md).

The `main`/`develop` split earns its keep for one reason: the artifact ends up in a
vehicle. "Stable" here means something concrete — the build you rely on at highway speed —
which is not true of most solo projects.

## Three things are versioned, independently

This is the distinction most likely to be collapsed by accident, and collapsing it would
quietly void the ecosystem promise.

### 1. The DashDeck app — SemVer, git tags

`v0.1.0`, `v0.4.2`, `v1.0.0`. Tagged on every merge to `main`. Stays `0.x` through P0–P2;
`1.0.0` is earned when the component SDK is stable enough that breaking it would be a real
cost (end of P2).

### 2. `DashDeck.Abstractions` — the `apiVersion`

The contract every component compiles against. It has **its own version and its own
lifecycle**, exposed to components as `apiVersion` in the manifest.

- **Bumps only when the component contract changes** — never because the app released.
- A **major** bump means existing components stop loading, and requires an ADR.
- A **minor** bump is additive and safe.
- The host declares which `apiVersion` range it supports and refuses components outside it
  with a clear message.

App `v1.4.0` serving `apiVersion 1.0` is normal and correct. If the SDK version ever
starts tracking the app version, every app release nominally breaks every component, and
the promise that a component written in P1 still loads in P4 is gone.

### 3. Each component — its own SemVer

Declared in `component.json`, alongside the `apiVersion` range it is compatible with.
Components are released independently of the host and of each other.

## Releases

A release is a **GitHub Release on a `main` tag, with the self-contained build attached as
a zip.**

This falls out of constraint C1 rather than being invented: deployment is "unzip a folder,"
and uninstall is "delete the folder." So the release asset *is* the install channel, and —
the part worth having on purpose — **rollback is unzipping the previous release.** For
software you depend on in a moving vehicle, a rollback path that requires no network, no
installer and no tooling is worth more than it looks.

Release notes say what changed from the driver's seat, not what changed in the code.

## CI

[`.github/workflows/ci.yml`](../.github/workflows/ci.yml) runs on every push and pull
request to `develop` and `main`, on `windows-latest` (the shell is WPF): build the
solution, build every component in `components/`, run all tests. Dependabot proposes
NuGet and Actions updates monthly, against `develop`.

Still planned:

- **Validate the signal catalog** — every referenced signal exists, every definition
  decodes its recorded fixture correctly.
- Run the trip computer against a scripted drive with a known fuel consumption and assert
  the computed figure. This is only possible because the data is synthetic (ADR-0005), and
  it is the single most valuable check in the suite.
