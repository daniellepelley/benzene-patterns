# CQRS and Read Models

A running implementation of
[docs/patterns/cqrs-read-models.md](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/cqrs-read-models.md):
two share-nothing write services that **cannot** answer "a tenant and all its users", and one read
model that can — built by projecting their events into a shape nobody on the write side is allowed
to hold.

This is the third leg of the trilogy. The [transactional outbox](../transactional-outbox/README.md)
makes sure the events are never lost; [choreography](../choreography/README.md) delivers them without
the emitter knowing who listens; CQRS turns that stream into a query the write model cannot serve.

## Status

| Piece | .NET | Go | TypeScript | Python |
|---|---|---|---|---|
| Write model (Tenant, User) | ✅ | — | — | — |
| Read model: projection + queries | ✅ | — | — | — |
| Rebuild by replay, forwards and backwards | ✅ | — | — | — |

## The claim, and how to check it yourself

> **A read model is derived, disposable, and rebuildable — and a projection that isn't an
> order-insensitive fold is none of those things.**

```bash
cd dotnet
docker compose up --build -d

TID=$(curl -s -X POST http://localhost:9480/tenants -H 'content-type: application/json' \
  -d '{"company":"acme"}' | jq -r .tenantId)
curl -X POST http://localhost:9481/users -H 'content-type: application/json' \
  -d "{\"tenantId\":\"$TID\",\"email\":\"a@acme.com\"}"
```

Two aggregates, two services, no shared database and no call between them. Then:

```bash
curl http://localhost:9482/tenants/$TID
```

```json
{"tenantId":"tnt-0dcde5eb","company":"acme","userCount":1,
 "users":[{"userId":"usr-04a3aa2b","email":"a@acme.com"}],"tenantVersion":1}
```

One indexed read. Without the read model this is a runtime fan-out — ask the Tenant service, ask the
User service, stitch — on every single request. The join has moved from **query time** to **event
time** and been paid for once, when the data changed.

Now throw the view away and rebuild it:

```bash
curl -X POST http://localhost:9482/rebuild -H 'content-type: application/json' -d '{}'
# {"eventsReplayed":8,"reversed":false}
```

Byte-identical. And then the part that actually proves it:

```bash
curl -X POST http://localhost:9482/rebuild -H 'content-type: application/json' -d '{"reverse":true}'
```

**The entire history replayed backwards** — renames before their creates, users before their tenants
— and the view is *still* byte-identical. That is the difference between a projection that survives
its first redelivery and one that quietly drifts. CI diffs the three views and fails if any pair
disagrees.

## Eventual consistency, made visible on purpose

The read model lags. On a laptop it lags by less time than it takes to type the next `curl`, which
means the property that matters most in production is the one a demo never shows. So this example
has a `PROJECTION_DELAY_MS` knob, and CI turns it on:

```bash
PROJECTION_DELAY_MS=3000 docker compose up --build -d

TID=$(curl -s -X POST http://localhost:9480/tenants -H 'content-type: application/json' \
  -d '{"company":"acme"}' | jq -r .tenantId)

curl -i http://localhost:9480/tenants/$TID   # 200 - the authority, always current
curl -i http://localhost:9482/tenants/$TID   # 404 - the view has not caught up
```

Both answers are correct. **Which side to read is a per-query decision**, and it is the everyday CQRS
decision:

- A screen that must show a user their own just-committed write → read the **core service**. It is
  the authority and it is current.
- A cross-aggregate or high-volume query where a second of lag is irrelevant → read the **read
  model**. It is the only thing that can answer at all.

Routing everything through the read model reflexively is the common mistake, and it is the one this
knob exists to make you feel.

Note also what a `404` from the read model *means*: either there is no such tenant, or the event has
not arrived. The read model genuinely cannot tell those apart, and it does not pretend to.

## What's here

```
dotnet/
  Contracts/               topics, events, and the view's shape
  Write/
    TenantService/         tenant:create, tenant:rename -> tenant:created, tenant:renamed
    UserService/           user:create                  -> user:created
  ReadModel/               projects both streams, serves the query, rebuilds on demand
```

The write services are textbook [core services](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/core-services.md):
one aggregate each, their own store, reference by id, and **no route to anything**. The Tenant
service has never heard of users; the User service holds a `tenantId` and never calls anyone to
check it. CI greps for a reference in either direction, and for any reference to the read model from
either — because "add a new view without touching a core service" stops being true the moment one
appears.

## The three things a projection has to get right

### 1. Every mutation is a fold

`UpsertTenant` and `AddUser`, never `IncrementUserCount`. Publish the same user event four times:

```bash
curl -X POST http://localhost:9481/users -H 'content-type: application/json' \
  -d "{\"tenantId\":\"$TID\",\"email\":\"a@acme.com\",\"emitTimes\":4}"
```

`userCount` is 1. A counter would say 4, be wrong by a silent margin that grows with every
redelivery, and disagree with its own rebuild.

`UseIdempotency()` is on the pipeline too, but it is belt and braces here rather than the
load-bearing thing it is in the [choreography example](../choreography/README.md). **A projection
that needs idempotency middleware to be correct is a projection that will not survive its first
rebuild** — a rebuild replays everything, and no middleware saves you from that.

### 2. Order is not guaranteed, so the fold must not care

Two independent services publish to one exchange. Nothing orders their events against each other,
and requeues reorder within a stream. So:

- Tenant events carry a **version**, and the fold applies last-writer-wins **by version**, not by
  arrival. A create replayed after its rename does not revert the name.
- A user event for a tenant the view has not seen creates a **stub row** rather than being dropped or
  buffered.

The reverse-replay rebuild is how both are tested, rather than asserted in a comment.

### 3. Not-caught-up must not look like data

A stub row reports `tenantVersion: 0`:

```json
{"tenantId":"tnt-notyet0001","company":"","userCount":1,"tenantVersion":0, …}
```

Without that field, `company: ""` reads as *this tenant has no name* instead of *this row has not
caught up yet*. Same characters on screen, opposite meanings, and only one of them is true.

## Where a rebuild replays from

A fanout exchange keeps nothing — it delivers to the queues bound at publish time and forgets. So the
replay source has to live on the write side, next to the data that produced it. Each write service
here keeps an append-only `EventLog` and exposes it at `GET /events`.

**That log is the one piece of this example a real deployment would replace outright.** In production
it is the [outbox table](../transactional-outbox/README.md) — the same rows the relay publishes from
— or a durable log (Kinesis, Kafka, an event store). The in-memory version stands in for it so the
rebuild runs on a laptop.

Two other honest simplifications in the rebuild:

- It **clears in place**. A production rebuild projects into a new store and swaps at the end, so
  reads keep working and a failed rebuild leaves the old view intact.
- It replays each service's log in that service's own order, with **no global ordering** across the
  two. That is deliberate: it is the same condition the live pipeline runs under, so if the folds
  have an ordering dependency, the rebuild is where it surfaces.

## Both halves of the read model in one process

`ReadModel/StartUp.cs` mounts Kestrel as a **peer worker** beside the RabbitMQ consumer:

```csharp
app.UseWorker(worker => worker
    .UseAspNet(asp => asp.UseMessageHandlers(), options => options.Urls = …)
    .UseRabbitMq(config, connectionFactory, rabbit => rabbit.UseIdempotency().UseMessageHandlers()));
```

So the projection (its write side) and the queries (its read side) share one process, one container
and one store, with no second Benzene container to keep in step. Splitting them into two deployables
over a shared database is the usual production shape — the projector scales on event volume, the
query side on read volume, which is half the point of CQRS — and nothing but the hosting would
change.

`UseAspNet` is why this example pins **0.0.2-alpha.6** where the others pin alpha.4; it does not
exist in alpha.4. The reason is written up in
[`Directory.Packages.props`](dotnet/Directory.Packages.props).

## The framework gap, again

Each write service carries a copy of `RabbitMqOverOutbound.cs` — the `OutboundContext` overload
`Benzene.RabbitMq` does not ship, written out. It is the same gap the
[choreography example](../choreography/README.md#two-framework-gaps-this-needed) documents, and it is
still present in alpha.6. Two more copies here takes this repo to **seven hand-rolled outbound
adapters across five patterns**, which is an argument for closing the gap upstream rather than for
getting better at copying the file.

## Be honest about what this demo isn't

- **Every store is in-memory**, including the event log. Restart a service and its history is gone.
- **There is no outbox.** These write services publish after committing to an in-memory store, so
  there is a window in which a crash loses the event — exactly the bug the
  [outbox example](../transactional-outbox/README.md) exists to fix. Composing the two is left where
  the pattern doc leaves it: as the shape of a real system, not of a single runnable demo.
- **The broker is RabbitMQ**, because it runs on a laptop. The pattern's realization is SNS/SQS or
  EventBridge; what changes is one route per write service and the inbound transport on the read
  model.
