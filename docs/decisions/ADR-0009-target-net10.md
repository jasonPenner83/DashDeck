# ADR-0009 — Target .NET 10 LTS

**Status:** Accepted · 2026-09-01
**Supersedes:** the runtime-version half of ADR-0001. The WPF choice there still stands.

## Context

ADR-0001 specified ".NET 9 + WPF". The framework analysis was sound; the version was not
checked against the support calendar, and it should have been before a line was written.

**.NET 9 reaches end of support on 10 November 2026** — roughly ten weeks after this
project started. It is a Standard Term Support release. Starting a new project on a
runtime that leaves support before the project reaches its first milestone is a mistake
with no upside.

**.NET 10 shipped 11 November 2025 and is Long Term Support through 14 November 2028.**

## Decision

Target **.NET 10** (`net10.0`, and `net10.0-windows` for the WPF host).

## Reasoning

Three years of support versus ten weeks. There is no trade-off to weigh here: nothing in
ADR-0001's reasoning depended on the runtime version, WPF is fully supported on .NET 10,
and the Fluent theme WPF gained in .NET 9 carries forward.

Correcting this now costs one line in `Directory.Build.props`. Correcting it after the
component SDK ships costs a coordinated migration of every component, because
`DashDeck.Abstractions` and every component that references it must target the same
runtime.

## Consequences

- All projects target `net10.0`; the WPF host will target `net10.0-windows`.
- Components must target `net10.0` — recorded in the component SDK docs, since it is part
  of the compatibility contract.
- The next runtime decision point is .NET 12 LTS, expected November 2028. Deliberately far
  away.
- **Process note:** the support calendar is now something to check before recommending a
  runtime, not after. This ADR exists partly as the record of that.

## Sources

- [.NET 8 and .NET 9 end of support — .NET Blog](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/)
- [.NET official support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
