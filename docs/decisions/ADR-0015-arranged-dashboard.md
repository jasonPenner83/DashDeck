# ADR-0015 — The dash is a user-arranged list of cards, paged

**Status:** Accepted · 2026-09-02
**Builds on:** [ADR-0012](ADR-0012-widgets-and-applets.md) (a widget is data, not code),
[ADR-0004](ADR-0004-request-arbiter.md) (one budget, shared) and Q17 (a full-screen view
covers the stage, which keeps running).
**Resolves:** F9 (the sixth band was empty behind a three-band stage). **Retires part of**
F3 — the shell no longer builds its widgets in a constructor, though they still do not
arrive from `plugins/`.

## Context

ADR-0012 decided that a widget **is** a JSON description — a label, a signal id, a rate, a
format, a size — and that the most common contribution should be writable without a
compiler. What it did not settle is who writes that JSON, where it lives, or what happens
when there is more of it than fits on a screen. Until now the answer was "the shell, in its
own constructor, six of them, in a fixed 3 × 2 grid".

The request was to add and edit cards, choose their data, and scroll the dash horizontally to
hold more of them.

One number shapes all of this: **the catalog defines eleven signals.** Six are already on the
dash. Paging is worth building because it will be needed — Ford's interesting PIDs are
undiscovered (R2) and non-vehicle sources are a planned direction (B4) — but two pages is the
honest ceiling today, and this should not be built as though it were five.

## Decision

### A card is an instance, not a definition

ADR-0012's JSON describes what a TRANS TEMP card *is*. What the user arranges is a different
thing: *"I put one here, two columns wide, in Fahrenheit."* They look identical while every
card is a built-in signal card, and stop being identical the moment a card arrives from
`plugins/`. So the two are kept apart now: `CardSpec` is an instance, `signal` is one kind of
source among the several a component host will bring, and the rest of the record — label,
size, style, unit, rate, priority — is already right for any of them.

The arrangement is **one ordered list**. Nothing stores a row or a page number; cards flow
into rows and rows into pages from the space available at the time.

### Paged, snapped, and never free-scrolling

A swipe moves exactly one page and locks there, with dots below. Not a `ScrollViewer`.

### Only the visible page declares its signals

A card off the current page withdraws its declaration and re-declares when it comes back.

### Render style and display unit are presentation, applied at the very edge

A percentage may draw as a bar; a card may show °F while the truck reports °C. The catalog,
the signal, the bus and anything recorded or replayed all stay in the truck's own units.

### Editing happens on the dash; each card gets a full-screen editor

A long press enters edit mode. The nav band — the only one reachable from the driver's seat —
becomes the edit toolbar, and the dash region above it stays pixel-identical, so what is
being arranged is exactly what will be read. Tapping a card opens a full-screen editor, which
is simply what Q17 already says a full-screen view is: it covers the stage, and the stage
keeps running underneath.

Every change applies and is written out immediately. There is no Cancel.

### The arrangement is its own file, beside the settings

`%LOCALAPPDATA%\DashDeck\dashboard.json`, next to `settings.json` and for the same reason
(ADR-0014): `publish.ps1` deletes and rewrites the application folder on every build, so
anything stored beside the executable would be destroyed by each update.

### A card whose signal is gone is kept and marked

Not dropped, not repaired. It renders `UNAVAIL` and names the missing signal.

## Reasoning

**Free scrolling is wrong in a vehicle, and wrong for the code.** On a moving truck a
kinetic-scrolling dash slides under a finger over every bump, and a page can come to rest
half way — at which point a card is half off the edge and *which page am I on* stops having
an answer. It stops having an answer for the code too, and the code needs one: the page you
are on is what decides which cards hold live declarations. Snapping makes the question
answerable, which is why the gesture decision and the budget decision are the same decision.

**Suspending off-page cards is not an optimisation, it is the thing that makes paging safe
to offer.** ADR-0004 divides one budget across everything declared, over a link whose real
ceiling is still unmeasured (Q12). Without suspension, "add as many cards as you like" would
mean degrading the six signals in front of you to pay for fourteen nobody can see — and it
would do it silently, since each card would simply be allocated less. The rule is instead:
*a card you cannot see asks for nothing.*

**Converting units at the edge keeps the data honest.** A unit preference that reached into
the catalog would make a recorded drive un-replayable against a differently configured dash,
and would put a conversion between the decode spec and the range check in
`SignalDefinition.InRange` — which is exactly how a wrong decode turns into a plausible
number instead of an obviously broken one. Converting in the card changes six characters of
rendering and nothing else. The bar fraction is computed *before* conversion for the same
reason: a bar 40% full in °C and 55% full in °F would be a genuinely misleading thing to put
on a windscreen.

**A bar is not a derived value.** ADR-0012 draws a line — widgets cannot do what a signal
cannot express: no history, no custom drawing, no derived values. A bar sits inside that
line: it is one signal drawn against the range the catalog already declares as a correctness
guard. A sparkline or a peak-since-start would sit outside it, and would be asking to be a
component.

**Flowing rows rather than fixing them at two resolves F9 for free.** The old comment claimed
*n* rows and *n*+1 gutters fill *n* bands exactly. They do at two — 2 × 155 + 3 × 20 = 370 —
and only at two; the identity is a coincidence of the numbers, not arithmetic. Fixing the row
height and letting the leftover fall into the bottom gutter means a three-band stage gets
three rows instead of two and a spare band, and a card is the same size wherever it lands.

**Immediate saving matches the rest of the app** and the reason is unchanged from ADR-0014: a
dash is closed by having its power pulled, so a setting that survives only a graceful
shutdown is not a setting. It also makes the editor's preview honest, because the preview
*is* the card, live on the real signal.

**Keeping a broken card is the loud failure.** ADR-0012 asks widget validation to fail loudly.
Deleting someone's card because a catalog file moved is the loudest possible failure to
notice and the worst to recover from — and the card cannot merely be left alone either, since
the arbiter throws on an unknown signal id by design.

## Alternatives

- **Free kinetic scrolling.** More familiar and more fluid. Rejected above, on both the
  vehicle and the budget grounds.
- **No paging — let the stage shrink and grow the rows instead.** Simplest, and no new
  gesture. Rejected as a *replacement*: it caps the dash at about nine cards and pays for
  every extra card with stage size. It is retained as a behaviour, since rows do grow when
  the stage takes fewer bands.
- **Drag to reorder.** The obvious gesture. Rejected for now in favour of ‹ › on each card:
  drag-and-drop inside a `Viewbox`-scaled touch surface is fiddly to get right, it competes
  directly with the page-turn swipe, and chevrons work with gloves on. Worth revisiting.
- **Do all the editing in Settings.** Easier to build and reachable with a keyboard. Rejected:
  you would be arranging a grid you cannot see while arranging it.
- **One global metric/imperial setting.** Probably matches how it will actually be used, and
  is one setting instead of one per card. Rejected because it cannot express speed in km/h
  beside coolant in °F, and because a per-card choice costs nothing extra once the conversion
  sits at the edge.
- **Gauges, sparklines, peak-since-start.** Genuinely more useful on a dash. Rejected here
  because they cross ADR-0012's line; they need their own ADR, and probably a component.
- **Store each card's row and column.** Rejected: the layout would then be wrong whenever the
  stage claimed a different number of bands, and every reorder would become a grid operation
  instead of a list one.
- **Keep building the six in the shell and only allow editing later.** Rejected — the file
  format and the instance/definition split are the parts the component host will inherit, and
  they are cheaper to get right before there are components than after.

## Consequences

- **Eleven signals is the real limit on how much dash there is to arrange.** Paging exists
  and works; there is not yet enough data to fill it. This is a catalog problem (R2, B4), not
  a UI one, and it should not be solved by inventing cards.
- **There are now two files in `%LOCALAPPDATA%\DashDeck\`** that must both survive an update,
  and both must tolerate being written by a different build. `JsonFile` is shared between them
  so they cannot drift on that promise.
- **A card can now be pointed at any signal at any rate**, including four cards at 4 Hz. The
  editor states the cost in words and shows what the arbiter actually allocated, but nothing
  refuses the request — the arbiter degrades under contention and that is its job (ADR-0004).
  If this turns out to be too easy to get wrong, the fix is a warning, not a cap.
- **`IVehicleSignals` is not enough for an editor.** It enumerates ids and nothing else, so
  the host now hands the card editor a projection of the catalog. The UI still never sees a
  `SignalDefinition` — no mode, no PID, no bus, no decode spec — and that boundary has to be
  held as more sources appear.
- **The component host inherits a shape rather than a blank page**, and also inherits a
  requirement: whatever a component contributes must be able to be activated and deactivated,
  because the page rule applies to it too.
- **The band grid's documented identity was wrong** and is now corrected in `BandGrid`. Any
  future re-derivation for a different tablet has to start from `RowsIn`, not from the
  "fills exactly" claim.
