# The Modular Monolith, and the Road Out of It

A running implementation of
[docs/patterns/modular-monolith.md](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/modular-monolith.md):
three modules that talk **only by topic**, deployed two ways from the same module code — as one
process, and as three — where the only thing that differs is the routing table.

## Status

| Deployment | .NET | Go | TypeScript | Python |
|---|---|---|---|---|
| Monolith (in-process routes) | ✅ | — | — | — |
| Extracted (three services) | ✅ | — | — | — |

## The claim, and how to check it yourself

> **Extraction is a wiring change, not a rewrite.**

Run the same order through both stacks:

```bash
cd dotnet

# Phase 0 - one deliverable
docker compose up --build -d
curl -X POST http://localhost:9080/orders -H 'content-type: application/json' \
  -d '{"customer":"alice","sku":"WIDGET","quantity":2,"unitPrice":9.99}'

# Phase n - the same modules, three deliverables
docker compose -f docker-compose.extracted.yml up --build -d
curl -X POST http://localhost:9081/orders -H 'content-type: application/json' \
  -d '{"customer":"alice","sku":"WIDGET","quantity":2,"unitPrice":9.99}'
```

Same response, same statuses, same compensation. The `deployment` field is the only deliberate
difference — it exists so you can tell which stack answered.

Then read the diff that produced it:

```bash
diff dotnet/Monolith/StartUp.cs dotnet/Services/OrdersService/StartUp.cs
```

Three routes changed transport:

```csharp
// Monolith/StartUp.cs
.Route(Topics.BillingCharge,   p => p.UseInProcess())
.Route(Topics.BillingRefund,   p => p.UseInProcess())
.Route(Topics.ShippingReserve, p => p.UseInProcess())

// Services/OrdersService/StartUp.cs
.Route(Topics.BillingCharge,   p => p.UseBenzeneMessageOverHttp(billingUrl))
.Route(Topics.BillingRefund,   p => p.UseBenzeneMessageOverHttp(billingUrl))
.Route(Topics.ShippingReserve, p => p.UseBenzeneMessageOverHttp(shippingUrl))
```

`Modules/` is untouched between the two. `PlaceOrderHandler` said `SendAsync("billing:charge", …)`
before and says exactly that after.

## What's here

```
dotnet/
  Contracts/            topic strings and DTOs - the ONE thing a module may reference from another
  Modules/
    Orders/             order:place - calls billing and shipping BY TOPIC, compensates on failure
    Billing/            billing:charge, billing:refund - owns its own store
    Shipping/           shipping:reserve - owns its own store
  Monolith/             phase 0: all three modules, in-process routes, one container
  Services/
    OrdersService/      phase n: Orders alone, routing over HTTP
    BillingService/     phase n: Billing alone, behind a BenzeneMessage endpoint
    ShippingService/    phase n: Shipping alone, behind a BenzeneMessage endpoint
```

The domain is deliberately small — place an order, charge a card, reserve stock — because the point
is the seam, not the shop.

## The rules, and where each one shows up

| # | Rule | Where you can see it |
|---|---|---|
| 1 | Modules only talk by topic | `PlaceOrderHandler` names topics and nothing else. CI greps for a module referencing another module and fails the build |
| 2 | Share-nothing data, from day one | `BillingStore` and `ShippingStore` are separate objects in one process. Nothing stops Orders reading them — which is why this one is a **review** rule, and the one a routing table cannot fix later |
| 3 | Payloads are messages, not objects | `Contracts/Payloads.cs` is DTOs only. The in-process transport **serializes by default**, so a violation fails in development rather than at extraction |
| 4 | Results, not exceptions | Billing returns `not-found` / `validation-error`, Shipping returns `conflict`, and Orders branches on those statuses. No exception type crosses a module line |
| 5 | Consumers are idempotent, eventually | `RefundChargeHandler` is idempotent — a second refund reports `Refunded: false`. Free now; load-bearing the moment a queue is involved |
| 6 | Version topics deliberately | Not exercised here; the topics are v1 throughout |

CI enforces 1 and 3 mechanically and asserts 4 and 5 behaviourally. 2 and 6 are yours to keep, as
the pattern says.

## Be honest about the wire

The example does not pretend distribution is free. Three things genuinely changed at extraction, and
the code was written on day one to survive all three:

- **Latency.** Three in-process dispatches became three HTTP round trips. The routing table is the
  list of exactly which calls got slower — read it before you flip it.
- **Partial failure arrived.** `service-unavailable` was a status the caller handled in theory. Now
  it can actually happen, and the handling code already existed.
- **The shared transaction is gone.** Charge-then-reserve was never one transaction, by rule, so it
  was already a compensating saga. Had rule 2 been broken in the monolith, this is where it would
  have surfaced as a consistency bug.

What did **not** change: call sites, message contracts, serialization, failure handling, or any test
of a module. That is the whole of the claim.

## One thing this needed that the framework doesn't ship

`Services/OrdersService/BenzeneMessageOverHttp.cs` is a ~50-line outbound middleware that POSTs the
BenzeneMessage envelope to another service's `/benzene-message` endpoint.

`Benzene.Clients.Http` already ships `HttpBenzeneMessageClient`, documented as *"the HTTP counterpart
of the AWS Lambda invoke path"* — but it is registered as an `IBenzeneMessageClient` and there is no
`UseBenzeneMessageOverHttp()` extension on `OutboundContext` to bind it into a route, the way
`UseSqs`/`UseServiceBus`/`UseInProcess` do. The adapter uses documented seams only.

**Four independent patterns in this repo have now needed it** — the real-time-risk map-reduce
coordinator, the transactional outbox, and the two-tier orchestrator all carry the same file, five
copies in total — which is the argument for closing the gap upstream and deleting every one.

## Package pinning

This example uses [central package management](dotnet/Directory.Packages.props) with
`CentralPackageTransitivePinningEnabled`, which pins transitive Benzene packages from one list. The
real-time-risk example predates it and carries a hand-written exact-version bracket per package per
project instead; the reason both exist is written up in that props file.
