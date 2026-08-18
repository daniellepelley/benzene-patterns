# The Transactional Outbox

A running implementation of
[docs/patterns/transactional-outbox.md](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/transactional-outbox.md)
— **Shape 1, change data capture** — plus a working reproduction of the bug it exists to fix.

## Status

| Shape | .NET | Go | TypeScript | Python |
|---|---|---|---|---|
| 1 — change data capture | ✅ | — | — | — |
| 2 — explicit outbox table | not started | — | — | — |
| Idempotent consumer | ✅ | — | — | — |

## The bug, reproducibly

Most write-ups ask you to take the dual-write problem on trust. This one has an endpoint for it.

```bash
cd dotnet && docker compose up --build -d

# Commit the order, then die before publishing - exactly what a pod eviction, a deploy or a
# throttled network call does.
curl -X POST http://localhost:9180/orders/naive -H 'content-type: application/json' \
  -d '{"customer":"lost","total":42.00,"crashBeforePublish":true}'

curl http://localhost:9184/notifications      # the order is committed; the event never happened
```

The order is real and nothing downstream will ever hear about it. At-least-once delivery does not
help — the emit did not occur at all. That is the gap, and no amount of trying harder in the handler
closes it, because the database and the broker will never share a commit.

## The fix

```bash
curl -X POST http://localhost:9180/orders -H 'content-type: application/json' \
  -d '{"customer":"alice","total":42.00}'

curl http://localhost:9184/notifications      # the event arrived
```

`PlaceOrderHandler` writes the row **and stops** — no publish, no second system, nothing to crash
between. The `orders` table has a change stream, so the committed write *is* the trigger: the relay
reads the stream and publishes `order:created`. The event is emitted if and only if the write
committed.

The two paths differ in exactly one thing, and you can see it in
[`DynamoDbTableProvisioning.cs`](dotnet/OrdersService/DynamoDbTableProvisioning.cs): `orders` has
`StreamEnabled = true`, `orders-naive` does not.

### The demo that makes it obvious

Stop the relay, place orders, start it again:

```bash
docker compose stop relay
for i in 1 2 3; do
  curl -X POST http://localhost:9180/orders -H 'content-type: application/json' \
    -d "{\"customer\":\"c$i\",\"total\":1$i.50}"
done
curl http://localhost:9184/notifications      # count: 0 - nothing is watching

docker compose start relay
sleep 5
curl http://localhost:9184/notifications      # count: 3 - all of them, from the stream
```

**A relay outage is a delay, not a loss.** The relay starts at `TRIM_HORIZON` — the oldest record in
the shard — so a change committed while it was down is still there when it comes back.

## The other half: idempotent consumers

An outbox guarantees at-least-once **emission**. It deliberately does not guarantee exactly-once,
because that is not achievable across two systems. "Each event takes effect once" is finished on the
consumer.

Restart the relay and watch:

```bash
docker compose restart relay
curl http://localhost:9184/notifications
# { "count": 4, "duplicates": 3, ... }   <- redelivered, and ignored
```

The consumer dedupes on the event id, and the event id is the **stream's own sequence number** — so
a redelivery after a failed publish carries the same identity as the original. An id minted per
publish attempt would defeat the whole thing.

In production this is `Benzene.Idempotency`'s `UseIdempotency()` over a distributed store; the
in-memory set here is the single-replica stand-in for the same discipline.

> **Outbox + idempotent consumers = nothing lost, nothing double-applied.**

## What's here

```
dotnet/
  Contracts/        payloads and topics
  OrdersService/    POST /orders        - the pattern: write only
                    POST /orders/naive  - the bug: write, then publish, with an optional crash
  Relay/            reads the orders stream, publishes order:created, retries until it lands
  Notifications/    consumes order:created idempotently; GET /notifications
```

### Failure handling in the relay is the point, not an afterthought

If a publish fails, the shard iterator is **not** advanced past that record — the same batch is
re-read and re-published until it succeeds. Advancing past a failed publish is precisely how an
outbox silently loses the thing it exists to protect.

That mirrors the real transport: process the batch sequentially, stop at the first failure, report
that sequence number as a partial-batch failure so Lambda checkpoints there and redelivers from it.
CDC is ordered on purpose — unlike the SQS adapter's concurrent fan-out — because change order
matters.

## The local substitute

In production the relay is a Lambda with a DynamoDB Streams event source mapping and a
`[Message("orders:INSERT")]` handler, a handful of lines, because Benzene's CDC transport unmarshals
the committed `NewImage` into a plain object for you. This slice polls the same stream with the AWS
SDK instead — emulating a Lambda event-source mapping locally costs a lot of moving parts for little
fidelity, the same call the real-time-risk example made and recorded. The wire shape is identical,
so swapping in the real Lambda host changes nothing downstream.

DynamoDB Local needs no AWS account.

## Not covered here

**Shape 2, the explicit outbox table** — for stores that are not change-captured, or events that are
not a 1:1 image of a row. The pattern doc describes it; this example implements Shape 1 only,
because on DynamoDB that is the one to reach for first and it is the one Benzene makes nearly free.
