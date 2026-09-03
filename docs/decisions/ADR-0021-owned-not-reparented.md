# ADR-0021 — Own the window, do not adopt it

**Status:** Accepted · 2026-09-02
**Supersedes in part:** [ADR-0020](ADR-0020-native-app-occupants.md) — native applications on
the stage stands; the mechanism does not. `SetParent` is replaced by ownership plus placement.

## Context

ADR-0020 put native applications on the stage by re-parenting them: `SetParent` the
application's window into an `HwndHost` so it became a *child* of the shell. It worked —
Character Map and NuvioDesktop both rendered — and it was janky, in ways that all trace to a
single call.

A child window:

- **is clipped by its parent**, so anything the application draws outside our rectangle is
  simply gone;
- **inherits the parent's DPI transform**, which is why `HwndHost` ignores the `Viewbox` the
  whole shell is drawn inside and an adopted window landed wrong on any scaled display;
- **shares the parent's input plumbing**, so focus needed `AttachThreadInput` and dialogs
  escaped as new top-level windows anyway.

Three separate defects, one cause. The stage was trying to *contain* something that had no
business being contained.

## Decision

**The application stays a real top-level window. The shell merely owns it and places it.**

`SetWindowLongPtr(GWLP_HWNDPARENT)` sets a window's **owner**, which is a much lighter
relationship than parenthood: an owned window stays above its owner, minimises with it, and is
destroyed with it — while keeping its own message loop, focus, DPI handling and native input.
Nothing is clipped and nothing is transformed.

**Placement is ours.** `OverlayHost` positions the window over the stage rectangle, computed
with `PointToScreen` on the stage's own element — which returns device pixels and accounts for
the `Viewbox` scale and the display's DPI on the way. It re-places on the shell moving,
resizing, changing state or re-laying out, with a quarter-second backstop for the cases none of
those cover.

**It hides when the stage is not showing**, because an owned window is top-level and would
otherwise float over Settings and the card editor.

**The caption and resize frame are stripped**, since the window is positioned rather than
dragged.

**Ownership is read back with `GetWindow(GW_OWNER)`**, not inferred from the setter's return
value — which is the *previous* owner, so failure and a previously-unowned window look
identical from it.

## Reasoning

**Owning is the relationship that was actually wanted.** "This window belongs to that window"
is exactly what a stage occupant is, and Windows has had a primitive for it for thirty years.
Re-parenting was reached for because `HwndHost` is the WPF-shaped answer, and WPF's shape was
wrong for something that is not ours to lay out.

**It fixes the Viewbox problem rather than documenting it.** ADR-0020 recorded as a known
limitation that a hosted window ignores WPF transforms and lands wrong on a scaled screen.
`PointToScreen` resolves the transform to real coordinates, so placement is correct at any
scale. The limitation is gone rather than tolerated.

**It makes the result verifiable.** A re-parented child was invisible to
`RenderTargetBitmap`, so a screenshot proved nothing and the picture had to be confirmed by
eye. A separate top-level window appears in an ordinary screen capture, so the arrangement can
be checked the way everything else on this dash is.

**Everything ADR-0020 got right is unchanged.** The job object still guarantees cleanup, the
five states are still reported honestly, and the fallback to *running outside the stage* is
still the design rather than an apology.

## Alternatives

- **Keep `SetParent` and fix its symptoms.** Focus via `AttachThreadInput`, DPI via manual
  scaling, clipping by sizing carefully. Rejected: three workarounds for one wrong primitive.
- **DWM thumbnails** (`DwmRegisterThumbnailProperties`). Live, GPU-composited, trivial — and
  display-only, with no input at all. Genuinely appealing for a glanceable picture-in-picture
  and useless for an application you need to touch. Worth remembering for a rear-view camera.
- **Windows.Graphics.Capture plus input injection.** Perfect visual integration, and it is
  building a local remote desktop: capture latency, GPU cost, and injecting input into another
  process is fragile and privilege-sensitive.
- **Register the shell as an appbar** (`SHAppBarMessage`) so Windows reserves screen space and
  every maximised window avoids it. The most "correct" split screen there is, and it modifies
  the desktop work area globally while running — closer to shell integration than C1 invites.
  Worth revisiting only if the overlay proves insufficient.

## Consequences

- **Two top-level windows, so two taskbar entries**, and alt-tab can put the application
  behind the shell. Ownership keeps it above its owner but does not make the pair a single
  entry. Not yet a problem on a tablet in a truck; it would be on a desk.
- **Placement is polled as well as evented.** A quarter-second backstop is cheap and covers
  window moves that raise no event we hook — but it is a poll, and it is the part most likely
  to want revisiting.
- **Applications that resize themselves fight the placement.** The backstop puts them back,
  which will read as a flicker if an application insists.
- **Hiding is now explicit.** A re-parented window disappeared with its host for free; an
  owned one has to be told, and forgetting would put a video player over the Settings screen.
- **`HostedWindow` is deleted.** There is one mechanism, not two with a preference between
  them.
