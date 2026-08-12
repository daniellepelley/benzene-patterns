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
| [Real-Time Risk & Trading Platform](real-time-risk/README.md) | 🚧 slice (2/6) | 🚧 slice (2/6) | not started | 🚧 slice (2/6) |

"slice (2/6)" = the runnable Trade Ledger + Risk Read Models slice (the first two of the six
services) builds and is smoke-tested; the four advanced services are not started in any language.
See the [pattern README](real-time-risk/README.md) for the per-service table and
[real-time-risk/PARITY-FINDINGS.md](real-time-risk/PARITY-FINDINGS.md) for why the split is where it
is.

## Status of this repo

Early and incremental. The [real-time risk platform](real-time-risk/README.md) started in .NET, and
the same runnable slice is now ported to **Go** (on the published `benzene-go` module) and **Python**
(from source, pending a PyPI release). Porting each service is itself new work wherever the target
port lacks an equivalent to `Benzene.EventSourcing` — which so far is *every* non-.NET port, the
headline finding in [PARITY-FINDINGS.md](real-time-risk/PARITY-FINDINGS.md). TypeScript is pending an
npm scope (`@benzene` is taken by an unrelated project) before it can consume a published package.

A note on the "published packages" convention below: it holds fully only for .NET (NuGet) today. Go
is consumed at a module-proxy pseudo-version (resolvable, untagged); Python from git source (no PyPI
release yet). Each port documents its actual consumption in its own README.

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
