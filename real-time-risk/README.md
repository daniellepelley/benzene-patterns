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
| Market-Data Aggregator | [Stream processing](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/streaming-processing.md) | not started | — | — | — |
| Valuation Service | [Choreography](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/choreography.md) | not started | — | — | — |
| Risk Coordinator | [Map-reduce](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/map-reduce.md) | not started | — | — | — |
| Pricing Service | [gRPC](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/service-communication.md) streaming | ✅ | — | — | — |

Built in the order the reference doc itself recommends ("Building it, in order" §): Trade Ledger
first (everything else derives from its events), then Risk Read Models (now the business can *see*
the book). The Pricing Service is out of that order deliberately — it is last on the reference doc's
list, but it is the only service in the platform with **no cloud dependency at all**, so it could be
built while the market-data transport decision below is still open. See [Roadmap](#roadmap) for
what's next and why it's sequenced this way.

## What's running today

**Trade Ledger** (`dotnet/TradeLedger`) — books a trade as an immutable event, appended to a
DynamoDB-backed event log (`Benzene.EventSourcing.DynamoDb`'s `DynamoDbEventStore`, one stream per
book). `POST /trades` / topic `trade:book`.

**Risk Read Models** (`dotnet/RiskReadModels`) — consumes the ledger table's DynamoDB Stream in the
background and projects it into a per-book, per-symbol position + realized-cash view. `GET
/books/{book}/positions` / topic `book:positions`.

**Pricing Service** (`dotnet/PricingService`) — a low-latency streaming price/greeks feed over
**gRPC**, for desks that need a live subscription rather than an event. Three of gRPC's four RPC
shapes, each an ordinary Benzene message handler: `GetPrice` (unary snapshot), `SubscribePrices`
(server-streaming subscription), `PriceStream` (bidirectional session whose watch list changes while
it is open).

```
  POST /trades  ┌───────────────────┐  DynamoDB Stream   ┌───────────────────┐  GET /books/{book}/positions
  ─────────────►│   Trade Ledger    │────────────────────►│  Risk Read Models │◄─────────────────────────────
                 │  [event sourcing] │   (trades:INSERT)   │  [CQRS]           │
                 └───────────────────┘                     └───────────────────┘
                          │
                          ▼
                 DynamoDB "trades" table
                 (one item per event, keyed by book + version)

                 ┌───────────────────┐  gRPC (HTTP/2, protobuf, streaming)
                 │  Pricing Service  │◄──────────────────────────────────── other desks
                 │  [gRPC streaming] │
                 └───────────────────┘
                 (no cloud dependency, no shared state - see below)
```

### Run it

```
cd dotnet
docker compose up --build

curl -X POST http://localhost:8081/trades \
  -H 'content-type: application/json' \
  -d '{"book":"desk-a","symbol":"AAPL","side":"Buy","quantity":100,"price":150.25}'

# The projection is eventually consistent - the response's ProjectedThroughVersion tells you whether
# it has caught up to the trade you just booked yet.
curl http://localhost:8082/books/desk-a/positions
```

The Pricing Service speaks gRPC, so `curl` cannot drive it — but **server reflection is on**, so
[`grpcurl`](https://github.com/fullstorydev/grpcurl) needs no `.proto` file:

```
# What's there
grpcurl -plaintext localhost:8083 list

# Unary: one snapshot. Known symbols are AAPL, MSFT, GOOG, AMZN, TSLA; anything else is a real
# gRPC NotFound, mapped from the handler's Benzene result status.
grpcurl -plaintext -d '{"symbol":"AAPL"}' localhost:8083 pricing.Pricing/GetPrice

# Server-streaming: a live subscription. Omit max_ticks for an unbounded stream (Ctrl-C to stop).
grpcurl -plaintext -d '{"symbol":"MSFT","max_ticks":5}' localhost:8083 pricing.Pricing/SubscribePrices

# Bidirectional: a session whose watch list changes while it is open. Each watch is answered with an
# immediate snapshot, then ticks arrive every 250ms until you half-close (Ctrl-D).
printf '{"symbol":"AAPL"}\n{"symbol":"TSLA"}\n' \
  | grpcurl -plaintext -d @ localhost:8083 pricing.Pricing/PriceStream

# It is also the only service here with a real health check - grpc.health.v1, bridged from Benzene's
# own IHealthCheck registrations.
grpcurl -plaintext -d '{}' localhost:8083 grpc.health.v1.Health/Check
```

No AWS account needed: the official [DynamoDB Local](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/DynamoDBLocal.html)
image (`amazon/dynamodb-local`) provides the table and its stream, genuinely free with no signup.
(LocalStack was the original choice here and would have been a fine one, but its community and pro
images merged in 2026 - `localstack/localstack:latest` now refuses to start without a free LocalStack
account and an auth token, which would have quietly broken the "no account needed" promise. Found by
actually running the Docker Compose stack in CI, not assumed.) `TradeLedger` provisions the table
itself at startup (idempotently, with retries - see `TradeLedger/DynamoDbTableProvisioning.cs`); there
is no separate init container.

### The Pricing Service's market data is simulated, and says so

In the reference platform this service is fed by the Market-Data Aggregator (§3), which is not built
yet — it is blocked on the transport decision in the roadmap below. So `PriceFeed.cs` generates
prices instead: a bounded ±1% wobble around a fixed reference price per symbol, hashed from
`(symbol, sequence)` rather than drawn from a clock or a shared `Random`. Deterministic on purpose —
the same symbol at the same sequence prices identically in every replica and on every run, which is
what lets the smoke test assert on a value, and means concurrent subscribers cost nothing to serve.

The greeks are real Black–Scholes, for a notional at-the-money 30-day call on the symbol rather than
for the cash equity — a share's own sensitivities are degenerate (delta 1, everything else 0), and
printing those would make the reference doc's "price/greeks feed" decorative rather than actual.

When the aggregator lands, swapping the simulated source for it is a change to one file: the handlers
take a symbol and a sequence and return a tick, and none of them knows where the price came from.

### A deliberate simplification for this local slice

In production, the Risk Read Models projection would run as a real AWS Lambda function triggered by
DynamoDB Streams' event source mapping (`Benzene.Aws.Lambda.DynamoDb`, `[Message("trades:INSERT")]` —
see that package's docs in the `benzene-dotnet` repo). Emulating that end-to-end locally means running
a Lambda executor too (LocalStack's or otherwise) - a lot of extra moving Docker-in-Docker machinery
for a local demo. Instead, `RiskReadModels/TradeStreamProjector.cs` is a small background worker that
polls the same DynamoDB Stream directly with the plain AWS SDK and dispatches to the *same* handler
shape (topic + JSON body) a real Lambda deployment would use. The wire contract is identical either
way; swapping in a real Lambda-hosted handler later is a hosting change, not a rewrite - exactly the
"same handlers, different host" property Benzene is built around.

## Roadmap

1. **Market-Data Aggregator + Valuation Service** — needs its own short design pass first: Benzene's
   windowed/partitioned/checkpointed streaming binding (`UseKinesisStream`) exists for Kinesis, Azure
   Event Hubs, and Cosmos DB change feed, but **not** Kafka (which only has a plain per-record
   consumer) - so "run it locally" isn't a drop-in transport swap here the way DynamoDB was. LocalStack's
   Kinesis emulation would keep the real `UseKinesisStream` code path, but factor in that LocalStack
   itself now requires a free account + auth token to even start (see above) - a real cost against the
   "no account needed" bar this repo otherwise clears. The alternative is a new `UseKafkaStream` binding
   in `benzene-dotnet` (a real framework enhancement, out of scope for just building this one demo).
   Decide before starting this service.
2. **Risk Coordinator** (map-reduce) — `Benzene.MapReduce`'s `ScatterGatherAsync` over Lambda-to-Lambda
   invoke in production; local Docker Compose needs its own substitute (LocalStack Lambda concurrency,
   or an HTTP-addressed worker pool) worked out on its own.
3. ~~**Pricing Service** (gRPC streaming)~~ — **done.** Built ahead of items 1 and 2 precisely because
   it has no cloud dependency and therefore no blocked design decision: it holds no state, talks to
   nothing, and needed no local substitute for anything. Its only stand-in is the simulated market
   data described above, which is one file.
4. **Go, TypeScript, Python** — once the .NET implementation of a service is done, port it. Each
   language gets its own `<pattern>/<language>/` folder with its own docker-compose. Porting is itself
   new work per language wherever that port doesn't yet have an equivalent to
   `Benzene.EventSourcing`/`Benzene.EventSourcing.DynamoDb` or DynamoDB Streams consumption - checking
   for that gap is part of each language's slice, not assumed away here.
5. Once ≥2 languages implement the same service, a shared black-box test suite (HTTP requests in,
   assertions on responses/read-model state) run against each language's compose stack would turn "the
   same system in every language" into something asserted by a real test, not just claimed.
