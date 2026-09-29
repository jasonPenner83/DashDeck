# Security policy

## Reporting a vulnerability

**Please don't open a public issue.** Use GitHub's
[private vulnerability reporting](https://github.com/jasonPenner83/DashDeck/security/advisories/new)
on this repository. Include what you found, how to reproduce it, and what it could affect.
This is a one-person project, so expect an acknowledgement within about a week.

Anything that could affect the **vehicle** — a path to writing to CAN, or a way to make
DashDeck interfere with the truck when it's closed — is treated as the highest priority.

## Supported versions

Only the latest `main` (the build on the truck) and `develop` get fixes.

## Security model — read this before installing someone else's component

DashDeck is honest about what it does and doesn't protect against.

| Area | What's true today |
|---|---|
| **Vehicle** | **Read-only.** No code writes to the vehicle. Future writes must pass the five gates in ADR-0006. |
| **Components (`plugins/`)** | **Not a security boundary.** A component is a .NET assembly loaded **in-process with full trust**. Its own `AssemblyLoadContext` isolates *faults* and versions, not permissions. It can do anything the logged-in user can. The manifest's permissions are not enforced yet. **Only install components you trust, the same as any program.** |
| **User-added stage apps** | Launch whatever executable the user configured. Only the user can add them. |
| **Web stage (WebView2)** | Loads the sites you choose. The page can send the host exactly one kind of message (a `drm:` status string); there is no vehicle-data bridge yet (B5 in the open questions). |
| **Phone GPS (`PHONE`)** | DashDeck connects **out** to the phone (Bluetooth COM port by default, or TCP). It opens no listening port. The NMEA stream is not authenticated, so on a shared network a location could be spoofed. It is displayed only and never controls anything. |
| **Weather** | Sends the configured latitude/longitude to Open-Meteo over HTTPS. No key, no account. |
| **Settings and trip data** | Stored locally in `%LOCALAPPDATA%\DashDeck`. Nothing is synced or uploaded. |

## For contributors

- Never commit secrets, API keys, VINs, precise home locations or personal data. Secret
  scanning with push protection is on for this repository.
- New network calls, listening ports, process launches or host bridges need a note in
  the PR and, if they change the model above, an update to this file.
