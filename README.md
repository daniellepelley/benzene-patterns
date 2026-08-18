# Benzene Patterns

Real, multi-language reference implementations of the composition patterns documented on
[benzene.app/docs/patterns](https://github.com/daniellepelley/Benzene/tree/main/docs/patterns) — the
same functional system, built with the same behaviour, in every Benzene language port. The patterns
docs explain the *shape* of a pattern; this repo proves it by actually running it, end to end, in
.NET, Go, TypeScript, and Python.

This is deliberately **not** in the [benzene](https://github.com/daniellepelley/Benzene) spec repo
(which is spec + website only, no implementations) or in any single language repo's own `examples/`
(which are intentionally small, single-concept demos). These are large, multi-service systems, and
they exist to prove the same system composes the same way across languages — so they get their own
home, shared across languages.

Each pattern is runnable locally via Docker Compose — no cloud account required.

## Patterns

| Pattern | .NET | Go | TypeScript | Python |
|---|---|---|---|---|
| [Real-Time Risk & Trading Platform](real-time-risk/README.md) | 🚧 in progress | not started | not started | not started |
| [The Modular Monolith, and the Road Out of It](modular-monolith/README.md) | ✅ | not started | not started | not started |
| [The Transactional Outbox](transactional-outbox/README.md) | ✅ | not started | not started | not started |
| [The Two-Tier Microservice Architecture](two-tier-architecture/README.md) | ✅ | not started | not started | not started |

## Status of this repo

Early and incremental. The [real-time risk platform](real-time-risk/README.md) is being built one
service at a time, in .NET first, per its own README's build order — see that document for exactly
what's running today versus what's planned. Four of its six services run today (Trade Ledger, Risk
Read Models, Pricing Service and Risk Coordinator), covering event sourcing, CQRS, gRPC streaming and
map-reduce. The two still to build — the Market-Data Aggregator and the Valuation Service it feeds —
are blocked on a transport decision that document records, rather than on the code.

## Conventions

- One top-level folder per pattern (matching a `docs/patterns/*.md` doc in the `benzene` repo).
- Inside each pattern folder, one subfolder per language (`dotnet/`, `go/`, `typescript/`, `python/`),
  each with its own solution/module and its own `docker-compose.yml` — a pattern's languages are
  independent stacks, not one shared compose file, since each language's services are its own images.
- Every language implementation consumes the target language's **published Benzene packages**
  (NuGet/Go modules/npm/PyPI) like any other consumer of the framework — no local source references
  back into the language repos. This repo is downstream of the language ports, not a sibling of them.
- A pattern's README documents its build order (which service to stand up first) and a status table
  per language, mirroring the shape of `docs/patterns/reference-real-time-risk.md`'s own "Building it,
  in order" section.
