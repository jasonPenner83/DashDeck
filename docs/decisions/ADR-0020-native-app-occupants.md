# ADR-0020 — Native applications on the stage, adopted where that works

**Status:** Accepted · 2026-09-02
**Relates to:** [ADR-0012](ADR-0012-widgets-and-applets.md) (web applets are the *third-party*
extension model) and [ADR-0019](ADR-0019-phone-projection.md), which rejected re-parenting for
OpenAuto — this admits it for a different case, and the difference is the point.

## Context

The NUVIO occupant pointed at `app.nuvio.tv`. That is a **third-party** web client, it is not
maintained by NuvioMedia, and it answered 526 when it was wired. The occupant was a link to
something that may never reliably exist.

The real application is [NuvioDesktop](https://github.com/NuvioMedia/NuvioDesktop): Kotlin
Multiplatform / Compose Desktop, shipped as an MSI, alpha by its authors' own description. A
JVM process with one real Win32 top-level window.

More generally, WebView2 is not the right host for everything. Some things exist as native
Windows applications and have no web version worth using.

## Decision

**A generic `AppStageOccupant`** launches an executable, waits for its main window, and
re-parents it into the stage with `SetParent`. `AppLaunchSpec` describes what to launch as
data — a name and a list of candidate paths, because MSI installers land in different places.

**The fallback is a first-class state, not an apology.** Whether an executable survives being
re-parented is a property of *that executable*, and cannot be known from here. So the occupant
reports which of five things happened — `NotInstalled`, `Starting`, `Hosted`, `Outside`,
`Failed` — and says so on screen.

**Everything launched goes into a job object** with `KILL_ON_JOB_CLOSE`.

**This is a launcher for applications you installed, not an extension mechanism.** ADR-0012's
answer for third-party code is unchanged: web applets, sandboxed by the browser.

**The NUVIO occupant becomes NuvioDesktop**, listed whether or not it is installed.

## Reasoning

**Re-parenting is acceptable here and was not for OpenAuto, and the difference is not taste.**
ADR-0019 rejected it because the alternative was a clean-room client for a documented protocol
that produced a picture we decode ourselves — strictly better, and it avoided a GPL question
as well. There is no such alternative for an arbitrary desktop application: the choice is
adopt it, launch it alongside, or do without. Adopting is the only one that keeps the stage a
stage.

**A separate process is a harder problem than airspace, and worth naming as different.**
WebView2 and LibVLC render into child windows of *our* process, which is why nothing composes
over them — the launcher bar and the action bar both exist because of it. Another process
brings its own window tree, message queue and focus model, and `SetParent` across that
boundary inherits all three.

**The job object is not optional.** The web occupant already carries the warning in its
`Dispose`: without it the hosted thing "outlives the occupant and keeps playing whatever was
on screen — audible, invisible, and impossible to stop from the dash". A separate process is
that failure with no upper bound, and `KILL_ON_JOB_CLOSE` covers the case a `finally` block
cannot — DashDeck itself being killed.

**Reporting the fallback beats hiding it.** An occupant that showed a black rectangle when
adoption failed would be indistinguishable from one that was broken, which is the same rule
every signal on this dash follows.

## Alternatives

- **Keep the web occupant.** Rejected: it points at an unmaintained third-party client behind
  a URL that answered 526.
- **Launch alongside, never adopt.** Robust with any application and needs no Win32 at all.
  Rejected as the default because DashDeck disappears behind it, and on a keyboard-less tablet
  getting back is a real problem — but it is exactly what the `Outside` state does when
  adoption fails, so it remains the fallback rather than being discarded.
- **Capture the window's frames and inject input.** A local remote desktop. Rejected: large,
  laggy, and input injection into another process is fragile and privilege-sensitive.
- **Do nothing and drop NUVIO.** Honest and cheap, and it leaves "the stage can only host web
  pages" as an unexamined limit.

## Consequences

- **A hosted window ignores the `Viewbox`.** `HwndHost` does not participate in WPF
  transforms, so on a scaled development screen an adopted window lands in the wrong place at
  the wrong size. On the tablet the `Viewbox` is 1:1 and it is invisible. Every child-window
  occupant already has this property; it is why none of them appear in a `--shot` either.
- **Verification needs a person.** `RenderTargetBitmap` cannot see an adopted window, so a
  screenshot proves nothing. `Describe()` reports the state, and the picture was confirmed by
  eye.
- **Store-packaged applications cannot be adopted at all.** Their `System32` executable is a
  stub that starts the real app in another process and exits — Windows 11's Notepad does
  exactly this, which is why the development probe is Character Map. The occupant reports
  `Failed` and says why.
- **DashDeck now launches arbitrary executables**, which is a real capability with no sandbox
  behind it. Acceptable because the user is pointing it at their own installed software; it
  would not be acceptable as a third-party extension point, and ADR-0012 still governs that.
- **A native occupant depends on something outside the folder deploy.** It does not break C1 —
  that constrains what DashDeck installs, not what the user does — but "copy the folder and
  run it" no longer describes every occupant.
- **Alpha software on a dash.** NuvioDesktop's stability is its authors' problem and now
  partly ours, since a crashing occupant is a crashing stage.
