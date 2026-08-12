# Real-Time Risk & Trading Platform — Python port

The Python port of the [Real-Time Risk & Trading Platform](../README.md)'s runnable slice: the
**Trade Ledger** (event sourcing) and **Risk Read Models** (CQRS off a DynamoDB Stream) services,
built on the [`benzene-python`](https://github.com/daniellepelley/benzene-python) framework. Same HTTP
contract, same eventual-consistency semantics, and the same Docker Compose shape (DynamoDB Local + two
services on ports 8081/8082) as the [.NET reference](../dotnet/).

## What runs

**Trade Ledger** (`trade_ledger/`) — `POST /trades` books a trade as an immutable event appended to a
DynamoDB event log (one stream per book, optimistic concurrency). Topic `trade:book`. It provisions
the `trades` table itself at startup (idempotently, with retries for the DynamoDB-Local warmup window).

**Risk Read Models** (`risk_read_models/`) — a background poller consumes the ledger table's DynamoDB
**Stream** and projects a per-book, per-symbol net position + realized-cash view; `GET
/books/{book}/positions` serves it. Topic `book:positions`. The projection is idempotent by
`(book, version)`, so the at-least-once stream (and a poller restart from `TRIM_HORIZON`) is replay-safe.

```
  POST /trades  ┌───────────────────┐  DynamoDB Stream   ┌───────────────────┐  GET /books/{book}/positions
  ─────────────►│   Trade Ledger    │───────────────────►│  Risk Read Models │◄─────────────────────────────
                │  [event sourcing] │   (trades:INSERT)  │  [CQRS]           │
                └───────────────────┘                    └───────────────────┘
                          │
                          ▼
                 DynamoDB "trades" table
                 (one item per event, keyed by book + version)
```

## Run it

```
cd real-time-risk/python
docker compose up --build

curl -X POST http://localhost:8081/trades \
  -H 'content-type: application/json' \
  -d '{"book":"desk-a","symbol":"AAPL","side":"Buy","quantity":100,"price":150.25}'

# The projection is eventually consistent — the response's projectedThroughVersion tells you whether
# it has caught up to the trade you just booked yet.
curl http://localhost:8082/books/desk-a/positions
```

No AWS account needed: the official [DynamoDB Local](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/DynamoDBLocal.html)
image (`amazon/dynamodb-local`) provides the table and its stream, genuinely free with no signup.

## Run the tests (local, no docker)

```
cd real-time-risk/python
python -m venv .venv && . .venv/bin/activate
# benzene-python isn't on PyPI (see below); install it from the local monorepo checkout for dev/tests:
pip install /workspace/daniellepelley/benzene-python/packages/benzene-results
pip install /workspace/daniellepelley/benzene-python/packages/benzene-core
pip install /workspace/daniellepelley/benzene-python/packages/benzene-http
pip install /workspace/daniellepelley/benzene-python/packages/benzene-aws
pip install /workspace/daniellepelley/benzene-python/packages/benzene-testing
pip install boto3 uvicorn pytest
python -m pytest
```

The unit tests cover the projection fold (buy/sell math, idempotency, ordering), the ledger handler
(happy path, validation, concurrency conflict) driven through the real HTTP front door with an
in-memory event store, the app-local event store's item shape + optimistic-concurrency mapping against
a recording fake boto3 client, and the HTTP path-param binding. The live DynamoDB integration is
covered by the CI smoke test (`.github/workflows/smoke-real-time-risk-python.yml`), not the local unit
tests (no docker daemon in the dev sandbox).

## Notes on parity (full detail in [PARITY-NOTES.md](PARITY-NOTES.md))

- **Source-consumption, pending PyPI.** benzene-python is **not on PyPI** — the release workflow
  exists but no version tag was ever pushed (PARITY-FINDINGS.md §1). The repo convention is *published*
  packages, which isn't yet possible for Python, so this port consumes the framework **from source**:
  the Docker images `pip install` the Benzene packages from git subdirectories pinned to commit
  `b073c95` (`requirements.txt`), and local dev/tests install them from the monorepo checkout. Swap to
  real PyPI versions once a release is cut.

- **App-local event store.** benzene-python ships **no event-sourcing package** (PARITY-FINDINGS.md
  §3.1) — .NET's `Benzene.EventSourcing.DynamoDb` has no Python analogue. So `trade_ledger/event_store.py`
  is an app-local `DynamoDbEventStore` implemented directly on boto3, matching the .NET item shape
  (`pk`/`version`/`eventType`/`payload`/`timestamp`) and the shared Terraform table, with
  `TransactWriteItems` + `attribute_not_exists(pk)` for optimistic concurrency. This is the single
  biggest parity gap this port confirms.

- **HTTP path-param binding: present.** `@http_endpoint("GET", "/books/{book}/positions")` **does**
  bind `{book}` into the handler's request — benzene-http's binding merges captured path params into
  the request object and maps them case-insensitively into the dataclass. No workaround was needed
  (unlike some other ports). See PARITY-NOTES.md §2.

- **Poller vs. Lambda.** On AWS, the Risk Read Models projection would run as a real Lambda triggered
  by a DynamoDB-Streams event-source mapping — benzene-python already ships that binding
  (`AwsLambdaApp` + `to_lambda_handler`, routing stream records to a `dynamodb:insert` topic). This
  local slice substitutes a background asyncio poller (`risk_read_models/projector.py`) that reads the
  same stream with boto3 and folds the same records; the wire shape is identical, so swapping in the
  Lambda-hosted handler later is a hosting change, not a rewrite. This mirrors the .NET design.
