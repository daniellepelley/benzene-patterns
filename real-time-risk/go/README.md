# Real-Time Risk & Trading Platform — Go port

The Go port of the Trade Ledger + Risk Read Models slice (see the pattern overview and cross-language
status in [../README.md](../README.md)). Same HTTP contract, same eventual-consistency semantics, and
the same Docker Compose shape (DynamoDB Local + two services) as
[the .NET reference](../dotnet), built on the published [benzene-go](https://github.com/daniellepelley/benzene-go)
framework.

## What runs

**Trade Ledger** (`cmd/trade-ledger`, package [`tradeledger`](tradeledger)) — `POST /trades` /
topic `trade:book`. Books a trade as an immutable event appended to a DynamoDB event log (one stream
per book, 1-based versions, optimistic concurrency). Owns the `trades` table and provisions it at
startup.

**Risk Read Models** (`cmd/risk-read-models`, package [`riskreadmodels`](riskreadmodels)) — `GET
/books/{book}/positions` / topic `book:positions`. A background goroutine polls the ledger table's
DynamoDB **Stream** and projects per-book/per-symbol net position + realized cash; the HTTP endpoint
serves that projection.

```
  POST /trades  ┌───────────────────┐  DynamoDB Stream   ┌───────────────────┐  GET /books/{book}/positions
  ─────────────►│   Trade Ledger    │───────────────────►│  Risk Read Models │◄─────────────────────────────
                │  [event sourcing] │   (trades:INSERT)   │  [CQRS]           │
                └───────────────────┘                     └───────────────────┘
                          │
                          ▼
                 DynamoDB "trades" table
                 (one item per event, keyed by book + version)
```

## Run it

```
cd real-time-risk/go
docker compose up --build

curl -X POST http://localhost:8081/trades \
  -H 'content-type: application/json' \
  -d '{"book":"desk-a","symbol":"AAPL","side":"Buy","quantity":100,"price":150.25}'

# The projection is eventually consistent - the response's projectedThroughVersion tells you whether
# it has caught up to the trade you just booked yet.
curl http://localhost:8082/books/desk-a/positions
```

No AWS account needed: the official [DynamoDB Local](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/DynamoDBLocal.html)
image provides the table and its stream, free with no signup. `trade-ledger` provisions the `trades`
table itself at startup (idempotently, with retries — see `tradeledger/provisioning.go`); there is
no separate init container.

## Local development (no Docker)

Everything compiles, vets, and unit-tests without a Docker daemon — the event-store/stream
integration is covered by the CI smoke test instead (`.github/workflows/smoke-real-time-risk-go.yml`):

```
cd real-time-risk/go
go build ./...
go vet ./...
go test ./...
```

The unit tests cover the projection fold (idempotency + buy/sell math) and the ledger handler
(happy path, per-book versioning, validation, concurrency-conflict mapping) against an in-memory
event store, plus the custom GET route-param adapter end to end.

## Three things worth knowing (Go-specific)

1. **The event store is app-local.** benzene-go has no event-sourcing package (unlike .NET's
   `Benzene.EventSourcing` / `.EventSourcing.DynamoDb`), so [`eventstore/`](eventstore) is the
   hand-rolled DynamoDB-backed store — conditional-write append + query read — writing the identical
   `pk`/`version`/`eventType`/`payload`/`timestamp` item shape as every other port and the shared
   Terraform table. This is the single biggest cross-language parity gap (PARITY-FINDINGS §3.1).

2. **The `{book}` route param needs a custom adapter.** benzene-go does not bind route params into
   the request model and exposes no handler-side inbound-header accessor, so
   `GET /books/{book}/positions` is served by a small custom `http.HandlerFunc`
   ([`riskreadmodels.PositionsHTTPHandler`](riskreadmodels/handler.go)) that extracts `book` from the
   path and dispatches `book:positions` **through the Benzene pipeline** via
   `envelope.DispatchTopicResult` — dispatch is not bypassed. See [PARITY-NOTES.md](PARITY-NOTES.md)
   #2.

3. **The projector is the local substitute for a Lambda.** On AWS this projection would be an
   `awsdynamodb.Handler` (topic `trades:INSERT`) hosted in a Lambda via `awslambda.Start`, triggered
   by a DynamoDB-Streams event-source mapping — exactly the .NET design. Locally,
   [`riskreadmodels.TradeStreamProjector`](riskreadmodels/projector.go) polls the same stream directly
   with the AWS SDK and applies records to the same store. The wire shape (topic + JSON body) is
   identical; swapping in the real Lambda-hosted handler later is a hosting change, not a rewrite.

See [PARITY-NOTES.md](PARITY-NOTES.md) for the full list of benzene-go framework gaps this port
worked around.

## Layout

```
real-time-risk/go/
├── contracts/          shared wire types + topics (camelCase JSON)
├── eventstore/         app-local DynamoDB event store (framework gap #1)
├── dynamoclient/       DynamoDB table + streams client builders (endpoint override)
├── tradeledger/        trade:book handler, app root, table provisioning
├── riskreadmodels/     projection store, book:positions handler + GET adapter, stream poller
├── cmd/trade-ledger/         main + Dockerfile
├── cmd/risk-read-models/     main + Dockerfile
└── docker-compose.yml
```
