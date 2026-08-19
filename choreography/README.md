# Event-Driven Choreography

A running implementation of
[docs/patterns/choreography.md](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/choreography.md):
one service emits an event, several independent services react to it, and **no service knows who the
others are**.

This is the deliberate counterpart to the [two-tier example](../two-tier-architecture/README.md).
There, an orchestrator directs six operations across three named services and a saga makes the whole
thing atomic. Here nothing is directed and nothing is atomic — and both are right, for different
work. The rule of thumb the pattern doc gives is the one to hold on to: **orchestrate what must be
atomic; choreograph what must merely happen.**

## Status

| Piece | .NET | Go | TypeScript | Python |
|---|---|---|---|---|
| Emitter (one route, one exchange) | ✅ | — | — | — |
| Three reactions | ✅ | — | — | — |
| A fourth reaction, added to a live estate | ✅ | — | — | — |

## The claim, and how to check it yourself

> **Adding a reaction changes no existing service.**

```bash
cd dotnet
docker compose up --build -d

curl -X POST http://localhost:9380/tenants -H 'content-type: application/json' \
  -d '{"company":"acme","email":"admin@acme.com","plan":"standard"}'
```

```json
{"tenantId":"tnt-35de56e1","company":"acme","emitted":1,"correlationId":"e7f53c06-0ba8-…"}
```

`202`, not `200`. The tenant exists; the welcome email, the warmed cache and the trial clock have not
necessarily happened yet, and claiming otherwise would be claiming a completeness choreography does
not offer. A moment later, all three have:

```bash
curl http://localhost:9381/emails    # {"count":1,"entries":[{"what":"welcomed admin@acme.com","correlationId":"e7f53c06-…"}]}
curl http://localhost:9382/warmed    # {"count":1,"entries":[{"what":"warmed tnt-35de56e1",     "correlationId":"e7f53c06-…"}]}
curl http://localhost:9383/trials    # {"count":1,"entries":[{"what":"trial started for tnt-35de56e1 (14 days)", …}]}
```

Now add the fourth reaction **to the running estate**:

```bash
docker compose --profile late up --build -d analytics
curl http://localhost:9384/signups   # {"count":0,"entries":[]}   <- a fanout has no history
```

Nothing else was rebuilt, restarted or edited. Emit one more event and the new service reacts along
with the other three. CI asserts exactly that, by comparing the container ids of the four running
services before and after.

The negative half of the claim is a grep, because that is the only way to prove a negative:

```bash
cd dotnet
git grep -niE 'welcomeemail|cachewarmer|trialclock|analytics' -- 'Emitter/*'   # finds nothing
```

`Emitter/StartUp.cs` has **one** outbound route. Compare the two-tier orchestrator's six, to three
named services.

## The other three things it demonstrates

### At-least-once is not a footnote

```bash
curl -X POST http://localhost:9380/tenants -H 'content-type: application/json' \
  -d '{"company":"initech","email":"admin@initech.com","plan":"standard","emitTimes":3}'
```

Three publishes of a byte-identical event, one reaction each. `UseIdempotency()` derives a key from
the topic and body, claims it atomically, and short-circuits the duplicates before the handler runs.
Every broker in this family — SNS, SQS, EventBridge, Service Bus, RabbitMQ — redelivers, so a
reaction that is not idempotent is a reaction that will eventually do its thing twice, and it will
do it quietly.

`emitTimes` is a demo affordance, not part of the pattern. It makes redelivery happen on demand
instead of on a bad day.

### A failing reaction isolates, and is not rolled back

```bash
curl -X POST http://localhost:9380/tenants -H 'content-type: application/json' \
  -d '{"company":"bounce-corp","email":"nobody@bounce.example","plan":"standard"}'
```

`202`. The tenant exists. The cache was warmed and the trial started. Only the email failed — and it
was retried on its own, because idempotency **releases** its claim when a handler reports failure
rather than letting a first bad attempt suppress every later one. After the bounded requeue the
delivery is nacked without requeue: to the queue's dead-letter exchange if it has one, and dropped
otherwise, which is the case here.

Nothing is undone, and that is the design rather than a gap. There is no central rollback in
choreography and reconstructing one out of scattered compensations is exactly the complexity a saga
exists to remove. **If the thing must not be half-applied, it does not belong here** — put it in an
[orchestrated saga](../two-tier-architecture/README.md) and let the saga emit an event when it lands.

### A reaction may decide it has nothing to do

An enterprise signup gets no trial clock. `TrialClock` consumes the event, returns success, and
records nothing. That decision lives in the service that owns the concept of a trial — putting it in
the emitter instead (`if plan == standard then also notify…`) is precisely how an emitter slowly
learns about its consumers and the decoupling evaporates.

## What's here

```
dotnet/
  Contracts/               the topic and the event payload - shared by both sides, owned by neither
  Emitter/                 creates the tenant, publishes tenant:created, stops
  Reactions/
    WelcomeEmail/          the one allowed to fail
    CacheWarmer/           the plainest one
    TrialClock/            the one that sometimes does nothing
    Analytics/             the one added later, behind a compose profile
```

Each reaction is a **Benzene RabbitMQ worker** (`UseRabbitMq`) hosted on the generic host, with a
one-line web endpoint beside it so a reader can see what it did. That endpoint is scaffolding, not
pattern: nothing in the system calls it.

Reactions share the `Contracts` project with the emitter and **nothing at all with each other** — CI
greps for a reference between any two of them. In a real fleet you would publish the event schema
rather than share a project; a shared project is the honest shortcut for a four-service demo, and it
is the only shortcut taken.

## Who declares what

| | Declares |
|---|---|
| Emitter | the `domain-events` **exchange**, and nothing else |
| Each reaction | its own **queue**, and the **binding** that feeds it |

There is no central topology file, and that absence is load-bearing. A shared topology template is
where "adding a reaction changes no existing service" quietly stops being true — the reaction is
independent but the file everyone has to edit is not. Each service declaring its own subscription is
the operational half of the decoupling.

The exchange is a **fanout**: every bound queue gets its own copy of every event, so a slow or
failing consumer holds up its own queue and nobody else's.

## Choreography is visible — and here is the wire that makes it so

The classic complaint about choreography is that the flow is written down nowhere. The
[pattern doc's answer](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/choreography.md)
is that the mesh derives consumer edges from **trace parentage**, so the graph draws itself from real
traffic. That is true, and it depends on a specific chain being intact, so this example checks the
chain rather than asserting it:

1. `UseCorrelationId(...)` and `UseW3CTraceContext()` stamp the emitter's outbound headers.
2. The publish adapter forwards those headers onto the AMQP message.
3. `UseW3CTraceContext()` on each reaction's inbound pipeline parses `traceparent` and makes the
   reaction's span a **child** of the emitter's.
4. Each reaction records the correlation id it received.

The emitter returns the correlation id it used; CI asserts the identical id turns up in all four
journals. A reaction whose span has no remote parent is a reaction the fleet view cannot connect to
the event that caused it — so this is the cheap, checkable form of the mesh claim.

## Two framework gaps this closed

Neither was a design choice of the example, and **0.0.3-alpha.2 shipped both fixes**.

**1. RabbitMQ had no `OutboundContext` overload.** `Benzene.RabbitMq` shipped the whole outbound path
— a context converter, a publish middleware, a `UseRabbitMq<T>()` extension — but that extension was
written against the older `IBenzeneClientContext<T, Void>` shape, while the outbound routing table's
pipelines are `IMiddlewarePipelineBuilder<OutboundContext>`. Every cloud transport had **both**
overloads (SQS, SNS, EventBridge, Service Bus, Event Grid, Event Hub, Queue Storage, Pub/Sub,
in-process); RabbitMQ, Kafka and HTTP had only the older one. `Emitter/RabbitMqOverOutbound.cs` was
that missing overload written out; the overload now ships and the file is gone, along with its two
copies in the CQRS example. The emitter's whole route is:

```csharp
.Route(Topics.TenantCreated, pipeline => pipeline
    .UseCorrelationId(WireHeaders.CorrelationId)
    .UseW3CTraceContext()
    .UseRabbitMq(channel, Broker.Exchange))
```

**2. Nothing restored the correlation id inbound.** `Benzene.Clients` stamped it on the way out, and
the diagnostics decorator read it onto the inbound span — but nothing put it back into
`ICorrelationId`, so a consumer's own correlation id was a fresh Guid and the chain broke exactly
where a reader would look for it. Each reaction carried six lines of pipeline to do it, four times
over. `Benzene.Diagnostics` now ships the inbound counterpart, and all four copies are one line:

```csharp
.UseW3CTraceContext()
.UseCorrelationId(WireHeaders.CorrelationId)
.UseIdempotency()
.UseMessageHandlers()
```

It is transport-agnostic in the same way `UseW3CTraceContext` is — it resolves the
`IMessageHeadersGetter<TContext>` for whatever context the pipeline carries — so the same line works
on a Kafka or SQS reaction unchanged.

A third, smaller one: the pattern doc's handler snippet shows `IMessageHandler<TenantCreated>`
returning `Task<IBenzeneResult>`. The shipped single-generic `IMessageHandler<TRequest>` returns
plain `Task`, which cannot report failure — and reporting failure is what makes a queue worker nack.
These handlers use `IMessageHandler<TRequest, TResponse>`; nothing reads the response payload, but
its **status** is what settles the delivery.

## Be honest about what this demo isn't

- **The journals are in-memory.** Restart a reaction and its history is gone. The point is the seam
  between emitter and reactions, not durability.
- **The idempotency store is in-memory**, so it de-duplicates within one process. A fleet of
  instances needs a shared `IIdempotencyStore` over an atomic conditional write — DynamoDB
  `attribute_not_exists`, Redis `SET NX`. Same interface, different implementation.
- **There is no dead-letter exchange.** A twice-failed delivery is dropped. Configuring a DLX and a
  redelivery policy is a broker-side decision, and leaving it out keeps the example honest about
  where the retry limit actually lives.
- **A late subscriber has no backlog.** A fanout delivers to the queues bound at publish time. When
  a new consumer genuinely needs history, that is a durable log — Kafka, Kinesis, an event store —
  and a different pattern.
- **The broker is RabbitMQ**, because it runs on a laptop. The pattern's AWS realization is SNS
  fan-out or an EventBridge bus. What would change is one route in `Emitter/StartUp.cs` and the
  inbound transport in each reaction; no handler and no contract moves.
