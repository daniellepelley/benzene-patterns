# benzene-go parity notes — Real-Time Risk Go port

Every benzene-go framework gap this slice (Trade Ledger + Risk Read Models) had to work around, so
it can feed back into the language repo as issues. These extend, and are consistent with, the
cross-language audit in [../PARITY-FINDINGS.md](../PARITY-FINDINGS.md); the section references below
point at it.

## 1. No event-sourcing package — the headline gap (PARITY-FINDINGS §3.1, §5.1)

.NET's Trade Ledger inherits `Benzene.EventSourcing` (`IEventStore`, `EventEnvelope`,
`AppendAsync(streamId, expectedVersion, events)`) and `Benzene.EventSourcing.DynamoDb`
(`DynamoDbEventStore`, the `pk`/`version`/`eventType`/`payload`/`timestamp` item shape,
`TransactWriteItems` + `attribute_not_exists(#pk)` optimistic concurrency). **benzene-go has no
analogue of any of it.**

**Worked around by** implementing the store as app-local code: [`eventstore/`](eventstore) is the
~150-line hand-rolled equivalent (conditional-write append + query read against aws-sdk-go-v2),
writing the identical DynamoDB item shape so it is wire-compatible with the .NET store against the
one shared Terraform table. Documented as app-local in the package doc comment.

**Fix upstream:** lift an event-sourcing package into benzene-go mirroring `Benzene.EventSourcing`'s
`IEventStore` + a DynamoDB store with the identical item shape.

## 2. Route parameters are not bound into the request model, and there is no handler-side accessor for inbound headers / route params

For `GET /books/{book}/positions`, `httpbinding` captures `{book}` and delivers it only as a
`route-book` **wire header** on `ic.Headers`. But benzene-go exposes **no public API for a handler
to read inbound headers or route params**: `InvocationContext` (which holds `Headers`) is stashed on
the context under an *unexported* key (`invocationContextKey{}`), and the only exported
handler-facing accessor is the **outbound** `benzene.SetResponseHeader`. So a normal handler
registered on that route literally cannot see `{book}` — it would receive an empty request body and
reject every call.

This is a real divergence from .NET, where ASP.NET model-binding drops the `{book}` route value
straight into `BookPositionsRequest.Book`.

**Worked around by** [`riskreadmodels.PositionsHTTPHandler`](riskreadmodels/handler.go): a small
custom `http.HandlerFunc` that (a) extracts `book` from `r.URL.Path`, (b) dispatches the
`book:positions` topic **through the same Benzene pipeline** via
`envelope.DispatchTopicResult(ctx, builder.Pipeline, builder.Container, topic, headers, body)` with a
synthesized `{"book":"<book>"}` body, and (c) maps the returned `wire.Response` to a native HTTP
response. Benzene dispatch is **not** bypassed — the handler and pipeline are exactly what a real
Lambda deployment would run; only the thin front-door adapter is bespoke.

**Fix upstream, either:** bind `route-<name>` headers into the request model (a `book` field would
fill from `route-book`), **or** export a handler-side inbound accessor (an `InvocationFromContext` /
`HeadersFromContext`, the read counterpart of `SetResponseHeader`).

## 3. `httpbinding.writeNativeResponse` is unexported

The `wire.Response` → native HTTP mapping (`w.WriteHeader(httpstatus.ToHTTP(...))` then write
`resp.Body`, plus response headers) lives in `httpbinding` as the unexported `writeNativeResponse`.
The custom GET adapter in #2 has to produce exactly that mapping, but cannot call it.

**Worked around by** copying the ~6-line function into `riskreadmodels/handler.go`. Harmless but a
maintenance seam — a facet of gap #2.

**Fix upstream:** export it (e.g. `httpbinding.WriteNativeResponse(w, resp)`) so a custom binding
that dispatches via `envelope` can render responses identically to the built-in `Handler`.

## 4. No local/standalone DynamoDB-Streams consumer — only the Lambda-shaped binding

benzene-go's DynamoDB-stream *consumer* is `awsdynamodb.Handler`, shaped for an AWS Lambda
event-source mapping (it parses a Lambda stream event and reports batch-item failures). There is no
framework helper for **polling** a stream directly outside Lambda, which is what the local
Docker-Compose slice needs.

**Worked around by** [`riskreadmodels.TradeStreamProjector`](riskreadmodels/projector.go): a
background goroutine polling `dynamodbstreams` (`DescribeStream` → `GetShardIterator` TRIM_HORIZON →
`GetRecords`) directly with aws-sdk-go-v2, applying each `TradeBooked` INSERT to the projection.

This one is **parity, not a Go-specific gap**: .NET's `TradeStreamProjector` is likewise app code
for the same reason (emulating Lambda + ESM locally is heavy), and on AWS both ports would host the
real `awsdynamodb.Handler` in a Lambda unchanged. Recorded only to note benzene-go offers no local
substitute either — the poller is hand-rolled in both languages.

## 5. (Context, not worked around here) one trigger per Lambda binary (PARITY-FINDINGS §3.4)

Not exercised by this local slice (which uses `net/http` + a goroutine, no Lambda), but relevant to
the shared deploy story: `awslambda.Start` takes a single handler, so on AWS the Go deployment would
be **more, smaller functions** than the .NET/TS/Python one-function-multi-trigger shape. The shared
Terraform must not assume a fixed function-to-service mapping across languages.

---

## Not a gap — confirmed working exactly as documented

- Core hosting (`benzene.App` three-phase lifecycle, `Register`, `NewPipeline` +
  `RouterMiddleware`, `httpbinding.Handler`) and DI (`AddSingleton` + `ScopeFromContext` +
  `GetService`) matched the task's stated idioms with no surprises.
- `benzenetest.NewHost` + `SendHTTP` drove the real `httpbinding.Handler` for the ledger tests
  cleanly.
- `Result` status → HTTP mapping via `httpstatus.ToHTTP` gave the exact codes the smoke test wants
  (200 ok, 400 bad-request, 409 conflict).
- The published module resolved from proxy.golang.org at the pseudo-version with no trouble; only
  the zero-dependency root-module packages were needed, so one `require` covered all of them.
