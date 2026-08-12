# Python port — parity notes

Framework gaps hit (and how they were worked around) while building the Trade Ledger + Risk Read
Models slice on `benzene-python` (`@ b073c95`, distributions all version `0.0.1`). This confirms and
extends the cross-language findings in [`../PARITY-FINDINGS.md`](../PARITY-FINDINGS.md) with what the
Python port specifically ran into. Verified against the framework source at
`/workspace/daniellepelley/benzene-python`.

---

## 1. Packages not publishable — consumed from source (confirms PARITY-FINDINGS §1, note ²)

benzene-python is **not on PyPI**. A trusted-publishing release workflow exists
(`benzene-python/.github/workflows/release.yml`, fires on `v*` tags) but **no tag was ever pushed**, so
`benzene-core` / `benzene-http` / `benzene-aws` / … all 404 on PyPI. The repo's convention (root
README) is that every port consumes its language's *published* packages; for Python that isn't possible
yet.

**Worked around by consuming from source, documented as such:**

- **Docker images** `pip install` the packages from git subdirectories pinned to commit `b073c95`, e.g.
  `benzene-core @ git+https://github.com/daniellepelley/benzene-python@b073c95#subdirectory=packages/benzene-core`
  (see [`requirements.txt`](requirements.txt) — also `benzene-results`, `benzene-http`, `benzene-aws`).
- **Local dev/tests** install the same packages from the monorepo checkout
  (`pip install /workspace/daniellepelley/benzene-python/packages/<dist>`), verified installable.

**Fix when possible:** cut a PyPI release and switch `requirements.txt` to version specifiers.

---

## 2. HTTP path-param binding — PRESENT (no workaround needed)

**The audited question:** does `@http_endpoint("GET", "/books/{book}/positions")` bind the `{book}`
path segment into the request object the handler receives?

**Answer: YES.** benzene-http's ASGI binding (`benzene/http/app.py`, `BenzeneHttpApp.handle`) matches
the route (`benzene/http/routing.py` compiles `{name}` to a named regex group), then **merges the
captured path parameters into the handler's request** — body < query < **path** (most specific wins) —
and `benzene.core.mapping.to_request` maps them **case-insensitively** into the request dataclass. So a
plain `request.book` on `BookPositionsRequest` is populated directly.

This port therefore uses the decorator form **directly**, with **no** ASGI-scope reach-around and **no**
custom route — unlike the Go port's gap the task flagged as a possibility. Proven by
`tests/test_risk_read_models_http.py::test_path_param_book_binds_and_selects_projection`, which drives
the real ASGI binding and asserts the captured `book` selected the right projection.

---

## 3. Event sourcing / DynamoDB event store — MISSING (confirms PARITY-FINDINGS §3.1)

benzene-python ships **no event-sourcing package**: there is no `IEventStore`, no optimistic-concurrency
`append`, no `EventEnvelope`, and no DynamoDB-backed store — nothing analogous to .NET's
`Benzene.EventSourcing` / `Benzene.EventSourcing.DynamoDb`. (Searched the framework source: no such
module exists; the capability matrix in PARITY-FINDINGS §1 marks it ❌ for Python.)

**Worked around with an app-local event store** ([`trade_ledger/event_store.py`](trade_ledger/event_store.py)),
implemented directly on boto3 and documented as app-local *because the framework lacks event sourcing*:

- **Item shape** matches .NET's `DynamoDbEventStore` defaults and the shared Terraform table
  (`../deploy/terraform/dynamodb.tf`): key `pk`(S, book) + `version`(N, 1-based); attributes `pk`,
  `version`, `eventType`(S), `payload`(S JSON), `timestamp`(S ISO-8601).
- **`read(stream_id)`** = `Query pk=:pk` ascending (paginated).
- **`append(stream_id, expected_version, events)`** = one `TransactWriteItems` with each `Put` guarded
  by `ConditionExpression="attribute_not_exists(pk)"` (on a composite-key table this checks the exact
  `(pk, version)` slot, giving optimistic concurrency); a cancelled transaction raises
  `EventStoreConcurrencyError`; returns the new highest version.

Item shape and concurrency behaviour are asserted in `tests/test_event_store.py`; the live round-trip
is the CI smoke test's job. This is the same ~100 lines every non-.NET port re-implements by hand — the
highest-value gap to close in the framework.

---

## 4. `BenzeneHttpApp` handles only `http` scopes — no lifespan hook

`BenzeneHttpApp.__call__` (`benzene/http/app.py`) raises on any ASGI scope type other than `http`,
including `lifespan` — so there is no framework seam to run per-process startup/shutdown (table
provisioning; starting and stopping the stream poller). .NET gets these from ASP.NET Core's host
lifecycle (`await ...EnsureTradesTableExistsAsync` before `app.Run()`; `AddHostedService<...>`).

**Worked around with a thin ASGI wrapper** ([`asgi_lifespan.py`](asgi_lifespan.py)): `LifespanApp`
answers the `lifespan` scope (running `on_startup` / `on_shutdown` coroutines) and delegates every
`http` scope straight through to the wrapped `BenzeneHttpApp`. It adds lifecycle *around* Benzene
dispatch and never intercepts or reroutes a request, so Benzene's routing/pipeline stays the sole
request path. Not a framework defect so much as a hosting concern the ASGI binding leaves to the host;
noted here because a future benzene-http lifespan hook would remove this wrapper.

---

## 5. Projection host: local poller substitutes for a Lambda DynamoDB-Streams trigger

Not a gap — benzene-python **does** ship the DynamoDB-Streams Lambda binding (`benzene/aws/app.py`
routes stream records via `dynamodb_record_envelope` to a `dynamodb:insert` topic; `to_lambda_handler`
produces the entry point). But emulating Lambda + an event-source mapping locally adds a lot of moving
parts, so this slice runs a background asyncio poller ([`risk_read_models/projector.py`](risk_read_models/projector.py))
that reads the same stream with boto3 and folds the same records. The wire shape (the `NewImage` a
handler sees) is identical, so a real Lambda-hosted projection is a hosting swap, not a rewrite —
exactly the .NET design and the "same handlers, different host" property Benzene is built around.

---

## 6. Minor: `to_jsonable` has no `Enum` branch — handled by design, not worked around

`benzene.core.mapping.to_jsonable` has no explicit `enum.Enum` case, so a bare `Enum` field would reach
`json.dumps` unserialized. Sidestepped by making `TradeSide` a `str, Enum` with values `"Buy"`/`"Sell"`
(the Python analogue of .NET's `[JsonStringEnumConverter]`): as a `str` subclass it serializes to its
token and compares equal to the bare string, so no custom encoder is needed. Worth a `str`-enum note in
the framework's serializer docs, but not a blocker.
