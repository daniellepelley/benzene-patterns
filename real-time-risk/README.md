# Real-Time Risk & Trading Platform

A real, running implementation of
[docs/patterns/reference-real-time-risk.md](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/reference-real-time-risk.md)
from the `benzene` spec repo: six services composing event sourcing, CQRS/read models, choreography,
stream processing, map-reduce, and gRPC into one platform. This repo builds it for real, one service
at a time, in every Benzene language port.

## Status

| Service | Pattern | .NET | Go | TypeScript | Python |
|---|---|---|---|---|---|
| Trade Ledger | [Event sourcing](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/event-sourcing.md) | ✅ | — | — | — |
| Risk Read Models | [CQRS & read models](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/cqrs-read-models.md) | ✅ | — | — | — |
| Market-Data Aggregator | [Stream processing](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/streaming-processing.md) | ✅ | — | — | — |
| Valuation Service | [Choreography](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/choreography.md) | ✅ | — | — | — |
| Risk Coordinator | [Map-reduce](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/map-reduce.md) | not started | — | — | — |
| Pricing Service | gRPC streaming | not started | — | — | — |

> **The Market-Data Aggregator does not build against a released package yet.** It depends on
> `Benzene.Kafka.Streaming`, which is [benzene-dotnet PR #17](https://github.com/daniellepelley/Benzene/pull/17)
> and has not merged or shipped to nuget.org. Until it does, that one package is built from source
> into a local folder feed — see [The unreleased dependency](#the-unreleased-dependency) before
> running anything.

Built in the order the reference doc itself recommends ("Building it, in order" §): Trade Ledger
first (everything else derives from its events), then Risk Read Models (now the business can *see*
the book), then the market-data half — ingest ticks, and react to the marks they produce. See
[Roadmap](#roadmap) below for what's next and why it's sequenced this way.

## What's running today

**Trade Ledger** (`dotnet/TradeLedger`) — books a trade as an immutable event, appended to a
DynamoDB-backed event log (`Benzene.EventSourcing.DynamoDb`'s `DynamoDbEventStore`, one stream per
book). `POST /trades` / topic `trade:book`.

**Risk Read Models** (`dotnet/RiskReadModels`) — consumes the ledger table's DynamoDB Stream in the
background and projects it into a per-book, per-symbol position + realized-cash view. `GET
/books/{book}/positions` / topic `book:positions`, plus the inverse read `GET
/positions/by-symbol/{symbol}` / topic `positions:by-symbol` ("who is exposed to AAPL?"), which the
Valuation Service asks on every mark.

**Market-Data Aggregator** (`dotnet/MarketDataAggregator`) — consumes a Kafka firehose of ticks with
`Benzene.Kafka.Streaming`'s `UseKafkaStream`, partitions each batch by topic-partition, rolls the
ticks into one-minute OHLC bars per symbol, and publishes each closed bar as a `bar-closed` event
through Benzene's outbound routing table. A pure worker — no HTTP surface.

**Valuation Service** (`dotnet/ValuationService`) — reacts to `bar-closed` (a per-record
`Benzene.Kafka.Core` consumer, not the streaming binding: one bar is one reaction), asks Risk Read
Models which books are exposed to the symbol, and marks each position to the bar's close.
`GET /valuations/by-symbol/{symbol}` / topic `valuations:by-symbol`.

**Tick Generator** (`dotnet/TickGenerator`) — a demo-only fixture, not part of the platform: there is
no real market-data feed behind this, so a tiny standalone producer walks a few hardcoded symbols and
puts synthetic ticks on `market-data-ticks`. Deliberately its own project and its own image rather
than a flag inside the aggregator, so no fake ever ships inside a real service — see its `Program.cs`.

```
  POST /trades  ┌───────────────────┐  DynamoDB Stream    ┌───────────────────┐  GET /books/{book}/positions
  ─────────────►│   Trade Ledger    │────────────────────►│  Risk Read Models │◄─────────────────────────────
                │  [event sourcing] │   (trades:INSERT)   │  [CQRS]           │  GET /positions/by-symbol/{s}
                └───────────────────┘                     └───────────────────┘◄──────────┐
                         │                                                                │ "who holds AAPL?"
                         ▼                                                                │ (HTTP)
                DynamoDB "trades" table                                                   │
                (one item per event, keyed by book + version)                             │
                                                                                          │
  ┌────────────────┐  market-data-ticks  ┌────────────────────────┐  bar-closed  ┌────────┴──────────┐
  │ Tick Generator │────────────────────►│ Market-Data Aggregator │─────────────►│ Valuation Service │
  │  (demo fixture)│  keyed by symbol    │  [stream processing]   │   (event)    │  [choreography]   │
  └────────────────┘                     └────────────────────────┘              └───────────────────┘
                                                                                          ▲
                                                                          GET /valuations/by-symbol/{s}
```

### Run it

```
cd dotnet

# One-off, until benzene-dotnet PR #17 ships - see "The unreleased dependency" below.
git clone https://github.com/daniellepelley/Benzene.git /tmp/benzene-dotnet
git -C /tmp/benzene-dotnet checkout claude/kafka-stream-binding
dotnet pack /tmp/benzene-dotnet/src/Benzene.Kafka.Streaming/Benzene.Kafka.Streaming.csproj \
  -c Release -p:PackageVersion=0.0.2-alpha.4 -o ./local-packages

docker compose up --build

curl -X POST http://localhost:8081/trades \
  -H 'content-type: application/json' \
  -d '{"book":"desk-a","symbol":"AAPL","side":"Buy","quantity":100,"price":150.25}'

# The projection is eventually consistent - the response's ProjectedThroughVersion tells you whether
# it has caught up to the trade you just booked yet.
curl http://localhost:8082/books/desk-a/positions

# The inverse read: every book exposed to a symbol. This is the query the Valuation Service makes.
curl http://localhost:8082/positions/by-symbol/AAPL

# The synthetic feed is already running, so a bar closes every BAR_INTERVAL_SECONDS (10s under
# compose - see the note in docker-compose.yml) and the Valuation Service marks the position to it.
curl http://localhost:8083/valuations/by-symbol/AAPL
```

The valuation response reports a **mark-to-market notional** (`netQuantity × close`) alongside the
realized cash the ledger has banked — and deliberately no unrealized-P&L figure. See
[What the valuation deliberately doesn't claim](#what-the-valuation-deliberately-doesnt-claim).

No cloud account needed, for either dependency. The official [DynamoDB
Local](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/DynamoDBLocal.html) image
(`amazon/dynamodb-local`) provides the ledger table and its stream; the official **Apache Kafka**
image (`apache/kafka`, single-node KRaft mode — no separate ZooKeeper container) provides the broker.
Both are genuinely free with no signup. (LocalStack was the original choice for DynamoDB and would
have been a fine one, but its community and pro images merged in 2026 — `localstack/localstack:latest`
now refuses to start without a free LocalStack account and an auth token, which would have quietly
broken the "no account needed" promise. Found by actually running the Docker Compose stack in CI, not
assumed. Redpanda was the other Kafka candidate and is lighter, but the official Apache image keeps
the broker under test identical to the one a reader actually runs, and single-node KRaft is small
enough that the weight difference doesn't bite. Confluent's `cp-kafka` was ruled out on licensing —
Confluent Community License, not Apache 2.0.) `TradeLedger` provisions the DynamoDB table itself at
startup (idempotently, with retries — see `TradeLedger/DynamoDbTableProvisioning.cs`); the two Kafka
topics are created by a one-shot `kafka-init` container, because auto-created topics would get a
single partition and silently collapse the shard-per-symbol property the streaming pattern is built
on.

## The unreleased dependency

`MarketDataAggregator` needs `Benzene.Kafka.Streaming` — the windowed, partitioned and
**checkpointed** Kafka stream binding. It is real, reviewed, building code, but it lives on
benzene-dotnet's `claude/kafka-stream-binding` branch as **PR #17** and has neither merged to `main`
nor been published to nuget.org, so `dotnet restore` cannot find it.

Rather than write the service blind against an API nobody can install, that one package is
`dotnet pack`ed from the branch into `dotnet/local-packages/` (gitignored) and restored from a local
folder feed declared in `dotnet/nuget.config`. Only that package is built locally: the branch point
*is* the 0.0.2-alpha.4 release commit and `Benzene.Kafka.Core` is untouched on it, so every other
`Benzene.*` package — including the `Benzene.Kafka.Core` this one depends on — still restores from the
real nuget.org release, exactly as TradeLedger and RiskReadModels do.

**When PR #17 merges and a release goes out:**

1. Bump every `[0.0.2-alpha.4]` pin in the `.csproj` files to the newly released version — the same
   one-line-per-package step the "Bump to Benzene 0.0.2-alpha.4" commit already made once.
2. Delete `dotnet/nuget.config`, drop `local-packages/` from `dotnet/.gitignore`, remove the pack
   step from `.github/workflows/smoke-real-time-risk-dotnet.yml`, and delete the pack command from
   this README and `docker-compose.yml`.

Do not guess that version number in advance — read it off nuget.org when it exists.

## Deliberate simplifications, and what they actually cost

### The Risk Read Models projector isn't a real Lambda

In production, the Risk Read Models projection would run as a real AWS Lambda function triggered by
DynamoDB Streams' event source mapping (`Benzene.Aws.Lambda.DynamoDb`, `[Message("trades:INSERT")]` —
see that package's docs in the `benzene-dotnet` repo). Emulating that end-to-end locally means running
a Lambda executor too (LocalStack's or otherwise) - a lot of extra moving Docker-in-Docker machinery
for a local demo. Instead, `RiskReadModels/TradeStreamProjector.cs` is a small background worker that
polls the same DynamoDB Stream directly with the plain AWS SDK and dispatches to the *same* handler
shape (topic + JSON body) a real Lambda deployment would use. The wire contract is identical either
way; swapping in a real Lambda-hosted handler later is a hosting change, not a rewrite - exactly the
"same handlers, different host" property Benzene is built around.

### In-memory rolling bar state is correct here, and this is not hand-waving

`docs/patterns/streaming-processing.md` is emphatic that rolling state across invocations belongs in
a store, not in memory — and it is right, *for the Kinesis/Lambda shape it works through*. Each Lambda
invocation is a fresh, stateless process that sees exactly one batch, so a bar spanning several
batches has nowhere to live but an external per-`(symbol, minute)` item.

`BenzeneKafkaStreamWorker` is a different animal: one continuously-running process consuming batch
after batch in the same address space. The store in the doc's example exists to survive the invocation
boundary, and there is no invocation boundary here — so `MarketDataAggregator`'s
`Dictionary<symbol, OpenBar>` genuinely *is* valid rolling state, not a shortcut. The condition it
rests on is that exactly one instance owns the state, which this single-instance Compose demo
satisfies by construction.

What it costs, stated plainly: scaling the aggregator past one instance breaks it (a rebalance would
migrate a symbol mid-window and split its bar), and a restart loses whichever windows were open — at
most one window per symbol. Fixing either means adopting the doc's external store, at which point its
Kinesis guidance applies verbatim. Replay *within* a running process is already handled:
`StreamReplayGuard` is an offset watermark that keeps the at-least-once redelivery the streaming
binding's retry policy produces from double-counting volume.

### What the valuation deliberately doesn't claim

The Valuation Service reports a **mark-to-market notional** (`netQuantity × close`) and the
**realized cash** the ledger has already banked. It does **not** report unrealized P&L, because the
data to compute one honestly does not exist yet: `PositionView` carries `NetQuantity` and
`RealizedCash` but no cost basis for the still-open position, and a true unrealized P&L needs the
weighted-average entry price of the open lots. Deriving one from `RealizedCash / NetQuantity` would be
wrong the moment a book has both bought and sold — which is every real book.

The response also carries `totalValue` (`marketValue + realizedCash`). For a book that started flat
that *is* total P&L, but that's a property of the book's history, not something this service can
assert — so it is named for what it computes. **Adding weighted-average cost-basis tracking to the
ledger projection, and only then a real unrealized-P&L field, is a named follow-up** (see the roadmap).

### A failed revaluation is skipped, not retried — and that's the right call here

Benzene's `MessageHandler` catches a handler exception and turns it into a `ServiceUnavailable`
*result* rather than letting it propagate, so the Kafka worker acknowledges the record either way;
`CommitOnlyOnSuccess` only withholds an offset when the *pipeline* throws. A `bar-closed` whose
read-model call fails is therefore logged and passed over.

That's fine here, and it was verified rather than assumed: with Risk Read Models stopped, each bar's
revaluation failed and was skipped, and the moment the read model came back the *next* bar produced a
complete, correct valuation for every exposed book. It works because this reaction is a **full
recomputation from current read-model state**, not an increment — a skipped bar costs one bar
interval of staleness and nothing accumulates wrong. The aggregator's fold *does* accumulate, which
is precisely why that side has real per-partition checkpointing and a replay guard and this side
doesn't need either.

### Two bits of glue that belong in the framework, not here

Both are flagged in code and should be raised upstream:

- `MarketDataAggregator/OutboundKafkaRouting.cs` — Benzene's outbound routing table
  (`AddOutboundRouting`/`Route`) has an `OutboundContext` overload for SNS, SQS, EventBridge, Event
  Grid, Event Hubs, Queue Storage, Service Bus and Pub/Sub. Kafka (and RabbitMQ) haven't been migrated
  to that shape yet — as of 0.0.2-alpha.4 the outbound `UseKafka<T>` still only extends the legacy
  `IBenzeneClientContext<T, Void>` pipeline, which nothing in the current clients design can build.
  The ~60-line adapter here is a faithful copy of `OutboundSnsContextConverter`'s shape and reuses
  `Benzene.Kafka.Core`'s own producer middleware; it should be deleted the day the framework ships it.
- `ValuationService/KafkaWorkerHosting.cs` — `app.UseWorker(...)` is a no-op unless the application
  builder is a `WorkerApplicationBuilder`, so a service that needs both a Kafka consumer *and* an HTTP
  query surface has to build the worker by hand against the web host's `IServiceCollection`. The
  Market-Data Aggregator, being worker-only, needs none of this.

### One more, smaller: `bar-closed`, not `bar:closed`

The reference doc names the event `bar:closed`, matching this repo's colon-separated topic
convention. Kafka topic names may only contain `[a-zA-Z0-9._-]`, and Benzene's Kafka binding routes a
record to a handler by matching the **literal Kafka topic name** against `[Message(...)]` — there is
no topic attribute on this transport to carry a different one. The Benzene topic and the Kafka topic
are therefore necessarily the same string, and it has to be Kafka-legal. The `:`-carrying topics are
unaffected: they only ever travel over HTTP or DynamoDB Streams.

## Roadmap

1. **Risk Coordinator** (map-reduce) — `Benzene.MapReduce`'s `ScatterGatherAsync` over Lambda-to-Lambda
   invoke in production; local Docker Compose needs its own substitute (LocalStack Lambda concurrency,
   or an HTTP-addressed worker pool) worked out on its own.
2. **Pricing Service** (gRPC streaming) — no cloud dependency, should be the most straightforward once
   the rest exists.
3. **Cost-basis tracking, and then real unrealized P&L.** The ledger projection folds trades into a
   net position and realized cash without keeping the open lots, so the Valuation Service can only
   honestly report a mark-to-market notional today (above). Tracking weighted-average entry cost in
   `PositionView` is a projection change; a real `unrealizedPnl` field follows from it.
4. **`position:revalued`.** The reference doc has the Valuation Service emit its own event so a limit
   monitor or hedging trigger can react without touching valuation. Left out of this slice on purpose:
   the query endpoint is what makes the flow demoable, a third hop proves nothing the second one
   didn't, and the outbound-Kafka glue it would need is the framework gap noted above — better to
   emit it once the framework ships that route than to duplicate the adapter into a second service.
5. **Go, TypeScript, Python** — once the .NET implementation of a service is done, port it. Each
   language gets its own `<pattern>/<language>/` folder with its own docker-compose. Porting is itself
   new work per language wherever that port doesn't yet have an equivalent to
   `Benzene.EventSourcing`/`Benzene.EventSourcing.DynamoDb`, DynamoDB Streams consumption, or a
   windowed Kafka stream binding - checking for that gap is part of each language's slice, not assumed
   away here.
6. Once ≥2 languages implement the same service, a shared black-box test suite (HTTP requests in,
   assertions on responses/read-model state) run against each language's compose stack would turn "the
   same system in every language" into something asserted by a real test, not just claimed.

### How the market-data half actually got built

The previous version of this roadmap said this slice "needs its own short design pass first", because
Benzene's windowed/partitioned/checkpointed streaming binding existed for Kinesis, Event Hubs and the
Cosmos change feed but **not** Kafka — which had only a plain per-record consumer — while the obvious
Kinesis-flavoured alternative (LocalStack) had just started requiring an account. Neither option
cleared the bar.

The resolution was the third option that pass named: a new `UseKafkaStream` binding in
`benzene-dotnet` ([PR #17](https://github.com/daniellepelley/Benzene/pull/17)). With it, the transport
swap is a genuine one rather than a downgrade — the same `PartitionBy`, the same `UseStream`, the same
per-partition checkpoint contract the Kinesis binding has, against a broker that runs locally with no
account. The reference doc's own "the same handlers, different host" framing is what makes that a
legitimate substitution and not a dodge, and the handler here would port to `UseKinesisStream`
essentially unchanged.

The two halves of the hop deliberately use *different* Kafka bindings, and that contrast is half the
point of the slice: the aggregator uses the **streaming** binding because ordering and rolling
aggregation over a firehose are the whole problem, and the Valuation Service uses the ordinary
**per-record** `UseKafka` because one closed bar is one independent, retryable reaction. Reaching for
the stream model on the second hop would trade away parallelism for ordering nobody needs — exactly
the mistake `streaming-processing.md`'s "stream vs. fan-out" table warns about.
