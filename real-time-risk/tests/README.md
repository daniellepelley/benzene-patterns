# Shared parity suite

[`parity-suite.sh`](parity-suite.sh) is one language-agnostic black-box test of the Trade Ledger +
Risk Read Models slice. It speaks only the HTTP contract — `POST /trades`, `GET
/books/{book}/positions` — so the **identical** script runs against every language's stack and asserts
the **identical** observable behaviour. That is the mechanism that turns "the same system in every
language" from a claim into a check.

It covers: a fresh book is empty at version 0; the first trade on a book is version 1 (event
sourcing); a buy projects `+qty` / cash-out and a sell nets quantity down / cash-in (the realized-cash
math); a second symbol is its own row and rows are symbol-sorted; books are isolated from each other;
an invalid trade is rejected with a 4xx and does not advance the ledger. Reads poll on
`projectedThroughVersion` because the read model is eventually consistent.

## Run it

Against a local compose stack (any language):

```bash
cd real-time-risk/<language>            # dotnet | go | python
docker compose up -d --build
LEDGER_URL=http://localhost:8081 READMODEL_URL=http://localhost:8082 \
  bash ../tests/parity-suite.sh
docker compose down -v
```

Against a deployed API Gateway, point both URLs at the same base URL.

## In CI

[`.github/workflows/parity-real-time-risk.yml`](../../.github/workflows/parity-real-time-risk.yml)
runs this one suite against a matrix of every built language (`dotnet`, `go`, `python`) — each brings
up its own compose stack and must pass the same assertions. Add a language to the matrix when its port
lands; the suite itself never changes.
