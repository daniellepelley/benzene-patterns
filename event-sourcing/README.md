# Event Sourcing

A running implementation of
[docs/patterns/event-sourcing.md](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/event-sourcing.md):
an account ledger where **the log is the truth**. There is no stored balance, no history table and no
audit system — state is a fold of events, computed on the way past and thrown away.

It is also the example that shows **where Benzene stops**. `Benzene.EventSourcing` is an append-only
store with optimistic concurrency and nothing else: no aggregate base class, no snapshot type, no
replay driver. Those you write, and this repo's job is to show what writing them actually looks like.
It comes to about a hundred and sixty lines.

## Status

| Piece | .NET | Go | TypeScript | Python |
|---|---|---|---|---|
| Append-only log, optimistic concurrency | ✅ | — | — | — |
| Rehydration fold, snapshots, point-in-time | ✅ | — | — | — |
| Upcasting historical events on read | ✅ | — | — | — |

## The claim, and how to check it yourself

> **State is a fold of the log — so the audit trail, the balance and "as of last Tuesday" are the
> same data, and cannot disagree.**

```bash
cd dotnet
docker compose up --build -d
B=http://localhost:9580

curl -X POST $B/accounts    -H 'content-type: application/json' -d '{"accountId":"acc-1","holder":"Ada","currency":"GBP"}'
curl -X POST $B/deposits    -H 'content-type: application/json' -d '{"accountId":"acc-1","pence":10000,"reference":"salary"}'
curl -X POST $B/withdrawals -H 'content-type: application/json' -d '{"accountId":"acc-1","pence":2500,"reference":"rent"}'

curl $B/accounts/acc-1
```

```json
{"accountId":"acc-1","holder":"Ada","currency":"GBP","balancePence":7500,
 "version":3,"eventsRead":3,"fromSnapshot":false,"snapshotVersion":0}
```

`7500` was not read from anywhere. It was computed from three events, this request, and discarded.
The audit trail is the same three events:

```bash
curl $B/accounts/acc-1/history
```

And "what did this account look like at version 2?" needed no design at all:

```bash
curl "$B/accounts/acc-1?asOf=2"    # 10000p - after the salary, before the rent
```

A log plus a pure fold **already is** a point-in-time query. `asOf` just stops the fold early.

## What each piece proves

### A concurrent write is rejected, not lost

Two clients read version 3 and both decide to withdraw £10:

```bash
curl -X POST $B/withdrawals -H 'content-type: application/json' -d '{"accountId":"acc-1","pence":1000,"expectedVersion":3}'
curl -X POST $B/withdrawals -H 'content-type: application/json' -d '{"accountId":"acc-1","pence":1000,"expectedVersion":3}'
```

The first gets `200`. The second gets `409`:

```
"concurrent-modification, Expected version 3 but the stream is at 4.,
 Re-read the account and decide again - this write was not applied."
```

The balance moves by £10, not £20. A store that took the last write would apply **both** decisions
against the same balance and lose one of them silently — which, in a ledger, means money that did not
exist. The handler does not retry: retrying would re-run a decision against state the caller never
saw, and that is precisely how "check balance, then withdraw" becomes an overdraft. The caller
re-reads and decides again.

### A refusal is a result, and appends nothing

```bash
curl -i -X POST $B/withdrawals -H 'content-type: application/json' -d '{"accountId":"acc-1","pence":999999}'
```

`422`, `insufficient-funds`, with the actual balance in the detail. No exception crosses a boundary,
and **no event is written** — the log records what happened to the account, not what somebody asked
for. (A domain that must audit refusals appends a refusal event deliberately; that is a domain
decision, and it belongs in the log the same way.) CI asserts the history length is unchanged.

### Old events, new code — and the log is never rewritten

The ledger used to be single-currency, so historical `money:deposited:v1` events have no currency
field. `POST /legacy-deposits` writes one, exactly as a build from that era would have:

```bash
curl -X POST $B/legacy-deposits -H 'content-type: application/json' -d '{"accountId":"acc-1","pence":5000,"reference":"1998-cheque"}'
curl $B/accounts/acc-1              # balance includes it, currency GBP
curl $B/accounts/acc-1/history      # still says money:deposited:v1, still has no currency field
```

The event is **upcast on read** and the stored bytes are untouched. That endpoint is a time machine
and the one dishonest thing in the example — no real system writes history in a retired format — but
the alternative was waiting a decade to demonstrate the property.

`Upcaster` here is app code because rehydration reads the store directly. For the **projection** half,
where events arrive as messages, Benzene ships `AddPayloadVersioning`, which does the same thing at
the pipeline edge and validates the caster graph at start-up — so a missing conversion path fails at
boot rather than on a 2015 event in production.

### A snapshot changes the cost, never the answer

```bash
curl -X POST $B/snapshots -H 'content-type: application/json' -d '{"accountId":"acc-1"}'
curl -X POST $B/deposits  -H 'content-type: application/json' -d '{"accountId":"acc-1","pence":100}'

curl $B/accounts/acc-1
# {"balancePence":11600,"version":6,"eventsRead":1,"fromSnapshot":true,"snapshotVersion":5}

curl -X POST $B/snapshots/clear -H 'content-type: application/json' -d '{}'
curl $B/accounts/acc-1
# {"balancePence":11600,"version":6,"eventsRead":6,"fromSnapshot":false,"snapshotVersion":0}
```

Same balance, same version, **one event read instead of six**. `eventsRead` is on the response for
exactly this reason: without it, a snapshot that is silently being ignored passes every test, because
both answers are right and only one of them was cheap.

Deleting every snapshot must change nothing but the cost. If it changed a balance, the snapshots
would be a second source of truth wearing a performance hat, and that is the failure mode this
endpoint exists to rule out.

## What's here

```
dotnet/Ledger/
  Events.cs         the event vocabulary, the historical v1 shape, and the upcaster
  Account.cs        the state, and the fold - (state, event) => state, no clock, no IO
  Rehydration.cs    read a stream and fold it; snapshots; point-in-time
  Commands.cs       open / deposit / withdraw: rehydrate, decide, append
  Queries.cs        current state, as-of, history, snapshot, clear snapshots
  Legacy.cs         the time machine
  StartUp.cs        one line of Benzene event sourcing, and the app code around it
```

## Where the hosting lives

`Program.cs` is the plain generic host, and contains no ASP.NET at all:

```csharp
var host = Host.CreateDefaultBuilder(args)
    .UseBenzene<StartUp>()
    .Build();

await host.RunAsync();
```

HTTP is declared in `StartUp.Configure`, with every other transport this service might grow:

```csharp
app.UseWorker(worker => worker
    .UseAspNet(
        http => http.UseMessageHandlers(),
        options => options.Urls = $"http://0.0.0.0:{configuration["PORT"] ?? "8080"}"));
```

`UseAspNet` runs Kestrel **as a Benzene worker**, exactly the way `UseSqs` or `UseRabbitMq` run their
consumers. So adding a queue consumer to this ledger later is another line in that method, and the
program's shape does not change.

The other shape — `WebApplicationBuilder.UseBenzene<StartUp>()` in `Program.cs`, `app.UseBenzene()`
after `Build()`, and `app.UseHttp(...)` in the startup — is for **embedding** Benzene inside a larger
ASP.NET application that has its own controllers or minimal APIs to serve. This ledger has none:
every route it answers is a Benzene handler, so ASP.NET is purely the HTTP host and belongs inside
the worker. (The [choreography](../choreography/README.md) reactions are the other case — they
genuinely do have a minimal-API endpoint of their own, so they use the embedded shape.)

## The line between framework and application

`StartUp.cs` has one event-sourcing registration:

```csharp
services.UsingBenzene(x => x
    .AddMessageHandlers(typeof(OpenAccountHandler).Assembly)
    .AddInMemoryEventStore());
```

That is the whole of `Benzene.EventSourcing`: `AppendAsync(streamId, expectedVersion, events)`,
`ReadAsync(streamId, fromVersion)`, `EventStoreConcurrencyException`. Everything else in this example
— `AccountFold`, `Rehydrator`, `SnapshotStore`, `Upcaster` — is application code, and the pattern doc
says so up front.

That is a considered line rather than a gap. Rehydration conventions, snapshot policies and replay
drivers vary enough between domains that a framework abstraction usually gets in the way, and the two
things that genuinely must be right — **append-only ordering** and **optimistic concurrency** — are
the two the framework does own.

Swapping to the DynamoDB store is one line (`AddDynamoDbEventStore("accounts")`), and nothing else
here moves; the [real-time-risk](../real-time-risk/README.md) example's Trade Ledger does exactly
that against DynamoDB with a CDC-driven projection.

## The fold is the thing to get right

`AccountFold.Apply` is a pure `(state, event) => state` with no clock, no store and no IO. That is
what makes every other claim on this page hold: the same events in the same order produce the same
account whether they are read live, resumed from a snapshot, replayed from the beginning of time, or
run in a unit test against a hand-written list.

Two details worth copying:

- It **upcasts first**, so the fold only ever knows today's shapes. A fold that carries a branch per
  historical schema accumulates one per year and is never safe to delete from.
- An **unknown event type advances the version and changes nothing else**. A newer writer may have
  appended something this build has no opinion about, and refusing to fold would leave an old reader
  unable to serve a stream it is otherwise perfectly capable of serving.

## Be honest about what this demo isn't

- **The store is in-memory.** Restart the container and the ledger is empty. That is the one line to
  change, and the reason it is in-memory is that it makes the concurrency assertions deterministic
  and keeps the example to a single container with no broker.
- **There is no projection here.** Feeding read models from the log is
  [CQRS](../cqrs-read-models/README.md), and it has its own example — including the rebuild-by-replay
  that the same pure fold makes safe. The [reference platform](../real-time-risk/README.md) wires the
  two together over DynamoDB Streams.
- **Snapshots are never taken automatically.** A real ledger snapshots on a policy (every N events,
  on a schedule). Making it an explicit endpoint is what lets the smoke test compare the two paths
  directly.
- **The concurrency demo is sequential.** Two requests, the second carrying a stale version — which
  is the same condition a genuine race produces, and reproducible, which a race is not.
