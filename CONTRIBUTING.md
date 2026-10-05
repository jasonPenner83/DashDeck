# Contributing to DashDeck

Thanks for helping. DashDeck is an **ecosystem**: the host exists so that adding a
capability means one small component against a stable SDK. Most contributions should be
components, catalog entries, or fixes, not changes to the host's architecture.

## Before you start

- **Read [`CLAUDE.md`](CLAUDE.md).** Despite the name, it is the project's working
  memory for every contributor, human or AI: current state, the rules, and the WPF traps
  that have already cost a day each.
- **Read the ADRs** in [`docs/decisions/`](docs/decisions/) before proposing an
  architectural change. Several alternatives were rejected for reasons the code doesn't
  show.
- **Open an issue first** for anything larger than a bug fix, so we can agree on the shape
  before you spend the time.

## Setup

Full details are in [docs/06-development-setup.md](docs/06-development-setup.md). In short:

- **.NET 10 SDK.** The engine builds anywhere; the WPF shell (`DashDeck.Host`) needs
  **Windows**.
- **No hardware is needed.** Everything runs on a synthetic 2019 F-150 (ADR-0005).

```bash
git clone https://github.com/jasonPenner83/DashDeck.git
cd DashDeck
git checkout develop
dotnet test DashDeck.slnx
dotnet run --project src/DashDeck.Host
```

Warnings are errors (`TreatWarningsAsErrors`), so a clean build is the bar.

## Branches and pull requests

| Branch | Means |
|---|---|
| `main` | **The build on the truck tablet.** Protected. Only tagged merges from `develop`, by the maintainer. |
| `develop` | Integration and testing. **Open your PRs here.** |
| `feature/*`, `fix/*` | Your work. Branch from `develop`. |

1. Fork, or branch from `develop` if you have write access.
2. Keep a PR to one change. Include tests. CI must pass (build + all tests on Windows).
3. Use [Conventional Commits](https://www.conventionalcommits.org/) the way the history
   does: `feat(stage): …`, `fix(dash): …`, `docs: …`.
4. Fill in the PR template, including the rules checklist.

Details on versioning (app, `apiVersion` and each component version independently —
ADR-0008) are in [docs/05-releases-and-branching.md](docs/05-releases-and-branching.md).

## The rules that aren't obvious from the code

These are the reasons a PR gets sent back. The full list, with context, is in `CLAUDE.md`.

1. **Read-only.** Nothing writes to the vehicle. A future write must pass all five gates
   in ADR-0006; a PR that writes to CAN outside that process will be closed.
2. **The truck must work without us.** Nothing may degrade the vehicle when DashDeck is
   closed or absent. SYNC 3 stays.
3. **Components never touch CAN.** They subscribe to named signals. If you need a PID, add
   it to the signal catalog (JSON in `catalog/`), not to your component.
4. **Nothing polls the adapter directly.** Components *declare* signals; the Request Arbiter
   builds the plan (ADR-0004). Don't assume request headroom — the real ceiling is unmeasured.
5. **Never guess a PID.** An undiscovered signal reads `Unavailable`. A wrong value that is
   in range can't be caught by any guard.
6. **Signal quality is rendered, never hidden.** Every value shows `Live` / `Stale` /
   `Unavailable` / `Simulated`. A confidently wrong number is worse than a blank.
7. **Inject `IClock`.** Never `DateTime.Now` — it breaks replay and scripted-drive tests.
8. **The tablet is a personal device.** No kiosk mode, services, registry writes, or new
   drivers. Deploy is a folder; settings live in `%LOCALAPPDATA%`. A new driver needs an ADR.
9. **Strict layering.** Transport → adapter → catalog → arbiter → state bus → services →
   component host → shell. No layer reaches past its neighbour.
10. **Portrait-first, glove-friendly.** Driving controls live in the bottom third. If a
    gesture must work on glass, say how you tested it on glass.
11. **Say how to test it in the truck.** A feature that touches the vehicle, the adapter or the
    screen in the cab adds a walkthrough to
    [docs/08-in-vehicle-testing.md](docs/08-in-vehicle-testing.md): what you need, numbered steps
    with what you should see, and what a failure looks like. Passing on the synthetic truck is
    not the same as working in the cab.
12. **Ship nothing vehicle-specific** (ADR-0052, [DISCLAIMER.md](DISCLAIMER.md)). Code and the
    shipped catalog hold only what public standards define (SAE J1979, ISO 15765, ISO 14229) and
    placeholders. A manufacturer's identifier, module address, bus rate or scaling goes in the
    user's own vehicle file in `%LOCALAPPDATA%\DashDeck\vehicles\` — never in a PR. Identifiers
    the protocol itself fixes (`7DF`, `7E8`, service `22`, the bitmap PIDs) may stay named constants.

## Writing a component

Start with **[docs/writing-a-component.md](docs/writing-a-component.md)**. The five
components in `components/` are working examples. A component:

- references **only** `DashDeck.Abstractions` (and optionally `DashDeck.Abstractions.Wpf`),
  with `Private=false` so no copy of the contract ships beside it;
- has a `component.json` manifest with its own id, SemVer and `apiVersion`;
- uses a reverse-DNS id you control (e.g. `io.github.yourname.thing`), not `com.jpenner.*`.

## Decisions and open questions

- A decision that changes the architecture gets an **ADR in the same PR** that implements
  it. ADRs are immutable once accepted; a changed decision supersedes the old one with a
  new ADR. Use the next number in `docs/decisions/`.
- Something undecided goes in [`docs/04-open-questions.md`](docs/04-open-questions.md)
  rather than being quietly resolved in code.

## Assets and licensing

- Contributions are accepted under the project's [MIT license](LICENSE).
- **Only commit images, fonts and other assets you have the right to publish.** No
  AI-generated images of real products, no downloaded photos, no manufacturer artwork.
  The TPMS component will use a `truck.png` dropped beside it locally; that file is
  git-ignored for this reason.
- A new package dependency goes in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) in the
  same PR.

## Security

Don't open a public issue for a vulnerability — see [SECURITY.md](SECURITY.md).

## Conduct

Be kind and assume good faith. See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
