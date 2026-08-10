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
| Pricing Service | gRPC streaming | not started | — | — | — |

Built in the order the reference doc itself recommends ("Building it, in order" §): Trade Ledger
first (everything else derives from its events), then Risk Read Models (now the business can *see*
the book). See [Roadmap](#roadmap) below for what's next and why it's sequenced this way.

## What's running today

**Trade Ledger** (`dotnet/TradeLedger`) — books a trade as an immutable event, appended to a
DynamoDB-backed event log (`Benzene.EventSourcing.DynamoDb`'s `DynamoDbEventStore`, one stream per
book). `POST /trades` / topic `trade:book`.

**Risk Read Models** (`dotnet/RiskReadModels`) — consumes the ledger table's DynamoDB Stream in the
background and projects it into a per-book, per-symbol position + realized-cash view. `GET
/books/{book}/positions` / topic `book:positions`.

```
  POST /trades  ┌───────────────────┐  DynamoDB Stream   ┌───────────────────┐  GET /books/{book}/positions
  ─────────────►│   Trade Ledger    │────────────────────►│  Risk Read Models │◄─────────────────────────────
                 │  [event sourcing] │   (trades:INSERT)   │  [CQRS]           │
                 └───────────────────┘                     └───────────────────┘
                          │
                          ▼
                 DynamoDB "trades" table
                 (one item per event, keyed by book + version)
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

No AWS account needed: the official [DynamoDB Local](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/DynamoDBLocal.html)
image (`amazon/dynamodb-local`) provides the table and its stream, genuinely free with no signup.
(LocalStack was the original choice here and would have been a fine one, but its community and pro
images merged in 2026 - `localstack/localstack:latest` now refuses to start without a free LocalStack
account and an auth token, which would have quietly broken the "no account needed" promise. Found by
actually running the Docker Compose stack in CI, not assumed.) `TradeLedger` provisions the table
itself at startup (idempotently, with retries - see `TradeLedger/DynamoDbTableProvisioning.cs`); there
is no separate init container.

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
3. **Pricing Service** (gRPC streaming) — no cloud dependency, should be the most straightforward once
   the rest exists.
4. **Go, TypeScript, Python** — once the .NET implementation of a service is done, port it. Each
   language gets its own `<pattern>/<language>/` folder with its own docker-compose. Porting is itself
   new work per language wherever that port doesn't yet have an equivalent to
   `Benzene.EventSourcing`/`Benzene.EventSourcing.DynamoDb` or DynamoDB Streams consumption - checking
   for that gap is part of each language's slice, not assumed away here.
5. Once ≥2 languages implement the same service, a shared black-box test suite (HTTP requests in,
   assertions on responses/read-model state) run against each language's compose stack would turn "the
   same system in every language" into something asserted by a real test, not just claimed.
