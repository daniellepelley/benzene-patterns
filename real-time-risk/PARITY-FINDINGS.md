# Cross-language parity findings — Real-Time Risk & Trading Platform

This document records what a real, evidence-based audit of the four Benzene language ports
(`benzene-dotnet`, `benzene-typescript`, `benzene-python`, `benzene-go`) found when we tried to build
the [six-service reference platform](README.md) *the same way in every language, on the same AWS
infrastructure*. It is the deliverable the exercise was really for: **not just "does it run", but
"are the language offerings actually up to spec, and where do they diverge?"**

The short answer: **the core hosting model has genuine parity across all four ports, but the four
advanced building blocks this particular platform leans on — event sourcing, windowed stream
processing, map-reduce over Lambda, and gRPC streaming — exist only in .NET.** Two of the four ports
also can't currently be consumed as "published packages" at all. None of this is a criticism of the
ports as general-purpose frameworks; it is exactly the gap analysis a reference app of this ambition
is designed to force out.

Audit date: 2026-08-12. Versions audited: `benzene-dotnet` @ `0.0.2` (NuGet `0.0.2-alpha.4`),
`benzene-typescript` @ `0.1.0` (source), `benzene-python` @ `0.0.1` (source), `benzene-go` @
`7a1e0a1` (proxy pseudo-version `v0.0.0-20260811220019-7a1e0a116aa5`).

---

## 1. Capability matrix

Legend: ✅ present & usable · 🟡 partial (usable core, a needed sub-feature missing) · ❌ missing ·
🏗️ build-it-yourself on top of the framework.

| Capability (service that needs it) | .NET | TypeScript | Python | Go |
|---|:---:|:---:|:---:|:---:|
| **Packages actually consumable today** | ✅ NuGet | 🟡 source only¹ | 🟡 source only² | 🟡 proxy pseudo-ver³ |
| Core hosting: message handlers + HTTP routing | ✅ | ✅ | ✅ | ✅ |
| DI / composition root (`StartUp`) | ✅ | ✅ | ✅ | ✅ |
| AWS Lambda hosting, **multiple triggers in one function** | ✅ | ✅ | ✅ | ❌ one trigger per binary |
| DynamoDB **Streams consumer** (CQRS projection) | ✅ | ✅ | ✅ | ✅ |
| **Event sourcing**: `EventStore`, optimistic-concurrency append, `EventEnvelope` (Trade Ledger) | ✅ | ❌ | ❌ | ❌ |
| **DynamoDB-backed event store** (Trade Ledger) | ✅ | ❌ | ❌ | ❌ |
| Choreography / event-handler subscribe (Valuation) | ✅ | ✅ | ✅ | ✅ |
| **Kinesis windowed/partitioned/checkpointed** stream processing (Market-Data Aggregator) | ✅ | 🟡 primitives unwired⁴ | 🟡 per-record only | 🟡 per-record only |
| **Map-reduce / scatter-gather over Lambda** (Risk Coordinator) | ✅ | 🟡 no coordinator⁵ | ❌ | ❌ |
| Outbound Lambda-invoke client (scatter target) | ✅ | ✅ | ❌ | ✅ single-target |
| **gRPC streaming** (Pricing Service) | ✅ (Kestrel only⁶) | ✅ 4 modes | 🟡 unary only | 🟡 unary only |

¹ **TypeScript** publishes nothing to npm — no publish workflow, packages ship raw `.ts` — **and** the
intended `@benzene/*` scope is already owned by an unrelated project (`hoangvvo/benzene`, a GraphQL
library: `@benzene/core@0.8.2`, `@benzene/http@0.4.2` are live on npm). The port cannot publish under
its own names without picking a new scope. Evidence: `benzene-typescript/.github/workflows/` (no
`npm publish`), `create-benzene/index.js:232-233` ("Until those are published to the registry…"),
and `registry.npmjs.org/@benzene/core`.

² **Python** has a real OIDC/trusted-publishing release workflow
(`benzene-python/.github/workflows/release.yml`, fires on `v*` tags) but **no tag was ever pushed**,
so `benzene-core`/`benzene-aws` etc. are **not on PyPI** (both 404). Consumable only from a git
checkout / local path.

³ **Go** has no semver tag (`RELEASING.md`: the `v0.1.0` tag "could not be pushed"), but the module
*is* resolvable through the Go module proxy at a commit pseudo-version, so it can be consumed with
`go get github.com/daniellepelley/benzene-go@<commit>`. Its per-SDK subpackages (`awssqs`,
`awslambdaclient`, …) are separate nested modules.

⁴ **TypeScript** actually ships the streaming engine — `window()`, `partitionBy()`,
`IStreamCheckpointer` in `@benzene/core-middleware` (`src/Benzene.Core.Middleware/Streaming/`) — but
it is wired to Azure Cosmos change feed, **not** to the Kinesis transport (`useKinesis` is per-record
fan-out; its own source note says "the streaming engine is not yet ported"). Closest of the three
ports to closing this gap.

⁵ **TypeScript** has the outbound Lambda-invoke client (`@benzene/clients-aws-lambda`) and
`BoundedFanOut`, but no scatter-gather *coordinator* that fans out and reduces. Python has neither a
coordinator nor a Lambda-invoke client. Go has a single-target invoke client (`awslambdaclient`) but
no fan-out/gather coordinator.

⁶ **.NET** gRPC is hosted only under ASP.NET Core / Kestrel — there is **no gRPC-on-Lambda** in any
port (the runtime model doesn't fit). See §3, "the architectural wrinkles".

---

## 2. Per-service portability verdict

Mapping the matrix onto the six services and the reference doc's own build order:

| # | Service | Pattern | Portable to TS/Py/Go on today's packages? |
|---|---|---|---|
| 1 | **Trade Ledger** | Event sourcing | 🏗️ **Yes, but** the event store is app-owned in every non-.NET port — the framework gives you nothing to inherit. This is the single biggest and most interesting parity gap: it's the same ~150 lines of "conditional-write append + query read" against the DynamoDB SDK, re-implemented per language, because no port has lifted it into the framework the way .NET has. |
| 2 | **Risk Read Models** | CQRS off DynamoDB Streams | ✅ **Yes.** DynamoDB-stream consumption is real in all four ports (`{table}:{event}` topic convention is even identical). The projection fold is app code anyway. |
| 3 | **Valuation Service** | Choreography | ✅ **Yes.** A `@message('...')` subscriber on SNS/EventBridge/DynamoDB-stream. |
| 4 | **Market-Data Aggregator** | Kinesis stream processing | 🟡 **Degraded.** You get per-record processing everywhere; the windowed/partitioned/checkpointed aggregation that is the *point* of this service must be hand-built in TS/Py/Go (TS has the unwired primitives to do it with least effort). |
| 5 | **Risk Coordinator** | Map-reduce over Lambda | 🏗️/❌ **Framework gap.** Only .NET has `ScatterGatherAsync`. TS can assemble one from its invoke client + `BoundedFanOut`; Python has no Lambda-invoke client at all; Go has single-target invoke only. |
| 6 | **Pricing Service** | gRPC streaming | ⚠️ **Not on Lambda, and streaming only in .NET/TS.** gRPC needs a long-lived HTTP/2 server (Kestrel / `@grpc/grpc-js` / a Go gRPC server), so it breaks the "everything is a Lambda" uniformity in *every* language. Streaming specifically is missing in Python and Go (unary only). |

**The runnable, provable-today slice in all four languages is services 1–3** (Ledger + Read Models +
Valuation). Services 4–6 are where "port the app" turns into "port the framework."

---

## 3. The architectural wrinkles the exercise surfaced

These are the "problems" the reference app was expected to uncover — recorded here so they can feed
back into the language repos as issues:

1. **Event sourcing is a .NET-only capability.** `Benzene.EventSourcing` / `.EventSourcing.DynamoDb`
   (`IEventStore`, `AppendAsync(streamId, expectedVersion, events)`, `TransactWriteItems` with
   `attribute_not_exists(#pk)` for optimistic concurrency, item shape `pk`/`version`/`eventType`/
   `payload`/`timestamp`) has no analogue in TS, Python, or Go. Since this is the framework's flagship
   "book of record" pattern, that's the highest-value gap to close. This repo's ports implement the
   store as app-local code (documented as such) so the Ledger can exist at all.

2. **"Consume published packages" is only true for .NET today.** The repo's own convention
   ([root README](../README.md)) is that every port consumes its language's *published* Benzene
   packages. Right now only NuGet delivers that. TS is blocked by a **name collision** on npm (needs a
   new scope), Python needs a release actually cut, Go needs tags pushed (or consumers pinned to a
   proxy pseudo-version). Until then the ports here consume from git/source and say so.

3. **gRPC doesn't fit Lambda.** The "it shouldn't matter that it runs in a Lambda" goal holds for five
   of six services but not the Pricing Service: gRPC streaming needs a persistent HTTP/2 listener, so
   it deploys as a container (ECS/App Runner/Fargate), not a Lambda, in *every* language. The shared
   Terraform therefore has to model "mostly Lambda, plus one long-lived service" rather than "six
   identical Lambdas."

4. **Lambda multi-trigger is not universal.** .NET, TS, and Python each let one Lambda host an API
   Gateway route *and* a DynamoDB-stream trigger in one function (event-shape dispatch). Go's
   `awslambda.Start` takes a single handler — one trigger per binary — so the Go deployment has more,
   smaller functions than the others for the same logical services. The shared infra must not assume a
   fixed function-to-service mapping across languages.

---

## 4. What this means for "shared Terraform, only the compilation changes"

The goal is sound and mostly achievable **for the Lambda-deployable services (1–5)** if the infra is
made language-opaque:

- **Package the handlers as container-image Lambdas**, not zip+runtime. A zip Lambda needs a
  per-language `runtime` (`dotnet8`/`nodejs20.x`/`python3.12`/`provided.al2023`) and `handler` string
  in Terraform — i.e. the infra *does* change per language. A **container image Lambda is just an
  `image_uri`**, identical in Terraform regardless of language; the Dockerfile is the only
  per-language artifact. That is the cleanest expression of "only the compilation changes."
- The DynamoDB table (+ stream), Kinesis stream, API Gateway, EventBridge bus, IAM, and event-source
  mappings are then **100% shared** — one root module, a `language` + `image_uri`-per-service variable,
  nothing else language-aware.
- The Pricing Service (gRPC) sits **outside** that shared Lambda module by necessity (wrinkle #3).
- Go's one-trigger-per-binary shape (wrinkle #4) is absorbed by letting the per-language image map
  expose either "one image, two triggers" or "two images, one trigger each" without the DynamoDB/API
  wiring caring which.

See [`deploy/`](deploy/) for the stack that implements this.

---

## 5. Recommendations fed back to the language repos

Concrete, in rough priority order (highest parity leverage first):

1. **Lift an event-sourcing package into TS, Python, and Go** — mirror `Benzene.EventSourcing`'s
   `IEventStore` + a DynamoDB store with the identical `pk`/`version`/`eventType`/`payload`/`timestamp`
   item shape, so the Trade Ledger stops being app-local everywhere but .NET.
2. **Publish the packages for real.** Pick a free npm scope for TS (the `@benzene/*` scope is taken);
   cut a PyPI release for Python; push Go tags. Then this repo can honour its own "published packages"
   convention.
3. **Wire the existing TS streaming primitives to Kinesis** (`window`/`partitionBy`/checkpointer →
   `useKinesis`) — the smallest step to a real Market-Data Aggregator in a non-.NET port.
4. **Port a `ScatterGather` coordinator** (and, for Python, a Lambda-invoke client first).
5. **Extend gRPC to streaming in Python and Go.**

---

## 6. Confirmed by actually building the ports (Go + Python)

The sections above are a static audit. Two ports of the runnable slice (Trade Ledger + Risk Read
Models) have now been **built and unit-tested** against the real frameworks, which both confirmed the
audit and turned up finer-grained gaps only implementation reveals. Each port carries its own
`PARITY-NOTES.md` ([go](go/PARITY-NOTES.md), [python](python/PARITY-NOTES.md)).

- **Go** ([`go/`](go/)) — consumes the genuinely-published `benzene-go` module (via its module-proxy
  pseudo-version; no tag needed). 20 tests green. Gaps hit: (a) event sourcing missing → app-local
  `eventstore/` with the shared item shape; (b) **route params are not bound into the request model and
  a handler has no way to read inbound headers/route params** (`InvocationContext` is under an
  unexported key; only outbound `SetResponseHeader` is exported) → `GET /books/{book}/positions` needs
  a thin custom-dispatch adapter; (c) `httpbinding.writeNativeResponse` is unexported so the adapter
  had to copy it.
- **Python** ([`python/`](python/)) — consumes `benzene-python` **from source** (git subdirectories
  pinned to a commit) because it isn't on PyPI. 24 tests green. Gaps hit: (a) event sourcing missing →
  app-local `event_store.py` with the shared item shape; (b) **`BenzeneHttpApp` rejects non-`http` ASGI
  scopes**, so there is no framework seam for per-process startup/shutdown (table provisioning, poller
  lifecycle) → a thin `LifespanApp` wrapper adds it *around* Benzene dispatch; (c) the serializer
  `to_jsonable` has **no `Enum` branch** → `TradeSide` is a `str`-enum to get `"Buy"`/`"Sell"` on the
  wire. Notably **Python's HTTP binding DOES bind `{book}` into the request model** — so it needs *no*
  route-param workaround, a concrete place where Python is ahead of Go.

Net: the biggest gap (event sourcing) reproduced in **every** non-.NET port exactly as predicted; the
smaller HTTP-binding gaps diverge *between* ports (Go lacks request route-param binding, Python has
it), which is itself a parity signal — the ports are not at a uniform maturity even on the basics.

Still outstanding (unchanged): TypeScript needs an npm scope (`@benzene` is taken — `@benzenejs`
chosen) + publish rights before it can consume a published package; Go/Python releases need a tag
pushed from an unrestricted environment (this repo's authoring sandbox blocks all tag creation).

---

*Generated as part of building this pattern's cross-language ports; superseded by the per-language
status tables in [README.md](README.md) as services land.*
