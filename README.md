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

| Pattern | Shape | .NET | Go | TypeScript | Python |
|---|---|---|---|---|---|
| [Real-Time Risk & Trading Platform](real-time-risk/README.md) | per-language | 🚧 in progress | not started | not started | not started |
| [Orchestrator (signup saga)](orchestrator/README.md) | **polyglot** | 🚧 orchestrator | ✅ tenant | ⛔ blocked | ✅ user |

## Status of this repo

Early and incremental. The [real-time risk platform](real-time-risk/README.md) is being built one
service at a time, in .NET first, per its own README's build order — see that document for exactly
what's running today versus what's planned. The [orchestrator saga](orchestrator/README.md) has its
two core services (Go, Python) built and verified; its .NET orchestrator, and therefore its compose
stack, are still to come. Each pattern's README is the authority on what actually runs today.

## Conventions

- One top-level folder per pattern (matching a `docs/patterns/*.md` doc in the `benzene` repo).
- A pattern is built in one of **two shapes**, and its README says which:
  - **Per-language** (the default) — one subfolder per language (`dotnet/`, `go/`, `typescript/`,
    `python/`), each with its own solution/module and its own `docker-compose.yml`. The languages are
    independent stacks, not one shared compose file, since each language's services are its own
    images. This shape proves *every port can express the pattern*, so the same system gets built
    once per language. [real-time-risk](real-time-risk/README.md) is this shape.
  - **Polyglot** — one stack whose services are each in a *different* language, with a single
    `docker-compose.yml`, and subfolders named `<service>-<language>/`. This shape is for patterns
    whose point only exists **between** languages — cross-language wire interop, where building the
    system once per language would demonstrate the opposite of the claim. Use it sparingly, and only
    when a per-language build genuinely could not show the same thing.
    [orchestrator](orchestrator/README.md) is this shape: a .NET orchestrator drives a saga across a
    Go service and a Python service over one wire contract, with no per-callee client code.
- Every language implementation consumes the target language's **published Benzene packages**
  (NuGet/Go modules/npm/PyPI) like any other consumer of the framework — no local source references
  back into the language repos. This repo is downstream of the language ports, not a sibling of them.
- A pattern's README documents its build order (which service to stand up first) and a status table
  per language, mirroring the shape of `docs/patterns/reference-real-time-risk.md`'s own "Building it,
  in order" section.
