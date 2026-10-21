# ADR-0061 — Updating from the dash: asked for, never offered

**Status:** Accepted · 2026-10-15
**Relates to:** constraint **C1** (nothing is installed on the tablet), **C5** (the dash is
launched by hand every time), and the release workflow added with v.0.9.0, which builds the
tablet deploy on a tag and attaches it.

## Context

The tablet deploy is a self-contained folder: unzip it anywhere, launch `DashDeck.Host.exe`,
delete the folder to uninstall. That is C1 taken literally, and it makes updating a copy job —
which has so far meant a laptop, a USB stick or a `git pull` and `publish.ps1`. None of that
happens in a truck, and the dash is the thing that knows it is out of date.

Since v.0.9.0 a tag push produces a release with `DashDeck-vX.Y.Z-win-x64.zip` attached. So the
newest build is reachable over HTTPS from the tablet, and the only thing missing is a way to ask.

## Decision

**Settings ▸ ABOUT ▸ UPDATE, with three buttons and nothing automatic between them.** CHECK FOR
AN UPDATE asks GitHub for the newest release. DOWNLOAD fetches the archive. INSTALL AND RESTART
replaces the folder and opens the dash again. Each step reports what it found and stops.

**Nothing is ever volunteered.** No check at launch, no check on a timer, no badge on the nav, no
toast, no "an update is available" anywhere — and nothing stored that could drive a reminder
later. The person pressing the button is the only signal that this is a good moment.

**The install is a folder swap, out of process.** A batch file in the temp folder waits for the
dash to exit, moves the current folder aside to `DashDeck.previous`, moves the unpacked copy into
its place, starts the executable again and deletes itself. If the second move fails the first one
is undone. One generation back is kept, not two.

**A build that cannot name itself is never told it is out of date.** `Directory.Build.props` now
carries `<Version>`, `publish.ps1` takes `-Version`, and the release workflow passes the tag — so
a deploy knows what it is. When the version cannot be read the screen says so and offers nothing.

## Reasoning

**A dash that nags is worse than a dash that is a version behind.** This thing lives on a
windscreen. An update prompt competes with the road for attention, arrives at the worst possible
moment by definition, and the cost of ignoring it is nothing at all. Checking on a timer also
means checking on a phone hotspot, and a 334 MB download that starts itself is a data bill. C5
already establishes that starting the dash is deliberate; updating it should be too.

**The decisions are pure and the plumbing is not, so they are separated.** Which version is
newer, which asset to fetch, whether the swap may proceed, and what the batch file says are all
in `DashDeck.Core.Updates` with 68 tests behind them. What is left in the Host is an HTTP request,
a download, a zip and a `Process.Start` — the parts that can only be proven on the tablet. The
version comparison in particular earns its tests: the tags say `v.0.9.0`, the assets say
`v0.9.0`, the assembly says `0.9.0+1a2b3c`, and `0.10.0` is newer than `0.9.0` by number and
older by alphabet.

**Moves, never copies; aside, never deleted.** A move within a volume changes a directory entry
rather than writing 300 MB, so the window where neither folder is in place is milliseconds. The
old folder is kept because the recovery has to work without a laptop: if the new dash misbehaves
in a parking lot, `DashDeck.previous` is a folder with a working executable in it.

**The guards are all about one failure.** Refusing a build folder, an unfinished download, an
archive that is not a DashDeck, or a volume without room for three copies — every one of them is
the same accident, which is a tablet that ends up with no dash on it somewhere inconvenient.

**C1 is intact.** Nothing is installed: no service, no scheduled task, no registry key, no
installer. The dash replaces its own folder, which is the same act as unzipping it by hand, and
the helper is a temp file that removes itself.

## Alternatives

- **Check at launch and show a quiet badge.** Rejected, and this was the closest call. A badge is
  not a prompt and would cost nothing on screen — but it still means a network request on every
  launch, which happens in a cold truck on a hotspot, and it still turns "is there an update" into
  something the dash decides to raise. Asked-for means asked-for.
- **Update from the git checkout** (`git pull` and `publish.ps1` behind a button). Rejected: it
  needs the SDK and a clone on the tablet, builds for minutes, and makes the dash depend on a
  toolchain that C1 would rather it did not.
- **An installer or an auto-updater framework** (Squirrel, Velopack, ClickOnce). Rejected on C1
  alone: all of them install something, and most of them want a service or a per-user app folder.
- **Download and install in one button.** Rejected: the download is minutes and the install closes
  the dash. Those are two different decisions and deserve two presses.
- **Delete the old folder rather than keeping it.** Rejected: it saves 334 MB and removes the only
  recovery that works without another computer.

## Consequences

- **The tablet needs the internet to update**, which in the truck means the phone's hotspot. The
  screen says so rather than failing mysteriously.
- **A failed swap leaves `DashDeck.previous` beside the folder**, and the script's log is in
  `%LOCALAPPDATA%\DashDeck\updates\swap.log`. Both are deliberately where a person can find them.
- **`%LOCALAPPDATA%\DashDeck\updates` accumulates** the last archive and the staged copy — about
  700 MB. Cleaning it up is not automatic yet, which is a known rough edge rather than a plan.
- **The swap itself cannot be tested here** (Q40). The script is pinned as text, including its
  line order and its rollback, but whether cmd, Windows file locking and a 1,700-file folder
  behave as expected is a tablet question. Until it is tried once, deliberately, on a desk, the
  honest description of INSTALL AND RESTART is "written and unproven".
- **Settings keys: none.** Nothing is remembered between checks, which is also what guarantees
  there is nothing to build a reminder out of later.

## Addendum 2026-10-18 — the first real swap failed, and why

The first update on the Surface (v.0.9.0 → v.0.10.0) downloaded and staged correctly, closed the
dash, and then nothing: no dash, and the old version when opened by hand. The script was right
about line order and wrong about where it was standing. **cmd inherits the dash's working
directory**, which is the deploy folder whenever the dash is started from its shortcut
(`publish.ps1 -Shortcut` sets it there) or from Explorer, and **Windows will not move a folder
that a process has as its current directory**. The very first move failed, the script logged
*nothing was changed* and stopped — correctly, by its own rules, and leaving the tablet with no
dash.

Three changes, none of which alter the decisions above:

1. **The script leaves before it does anything** — `cd /d "%TEMP%"` is its first command, and
   `UpdateService` starts it with the temp folder as its working directory too.
2. **The move aside is retried**, once a second for 15 s. After the dash exits, the map's browser
   (which serves its page from the deploy folder) or an antivirus can hold a file for a moment.
3. **Every failure after the dash has exited opens the old dash again**: a previous copy that
   would not delete (now checked, because `move A B` with `B` present puts `A` inside `B`), a
   folder that would not move aside, and the rollback. A failed update now looks like nothing
   happened, not like a dash that has gone. The one exception is a dash that never exited, which
   is still open. The relaunch uses `start /D` so the new dash starts in its own folder, the same
   as its shortcut.

The consequence above still stands with one word changed: INSTALL AND RESTART is "written, failed
once, fixed, and unproven" until a swap is seen to complete on the tablet.

## Addendum 2026-10-21 — a swap completed on the tablet

v.0.10.1 → v.0.11.0 on the Surface, through **INSTALL AND RESTART**: the dash downloaded, closed,
swapped its folder and **came back by itself on 0.11.0**, which Settings ▸ ABOUT confirmed. It is
the first update to go all the way through on Windows, and the first run of the 2026-10-18 fixes.

INSTALL AND RESTART is now **proven once**: one release, one tablet, one deploy started from its
shortcut. What is still untried is the failure half — the relaunch of the old dash after a swap
that fails — which by design only shows itself when something goes wrong.
