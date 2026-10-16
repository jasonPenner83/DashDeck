# plugins

Where a built component lands: one folder per component, each with its `component.json` and its
assembly. The host reads this folder at launch (ADR-0023) and loads whatever is here.

**It is empty on purpose, since 2026-10-15.** The five components that lived here —
fuel economy, range estimator, TPMS, trip computer, trip economy — are parked on the
**`parked/components`** branch along with their sources in `components/`, because every one of
them timed out during loading and held the launch open for forty-five seconds (Q42). Nothing is
wrong with the host: it loads an empty folder instantly, and component loading is bounded at ten
seconds now either way.

Bringing them back is `git merge parked/components`, a rebuild, and an answer to Q42.
