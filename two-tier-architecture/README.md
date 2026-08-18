# The Two-Tier Microservice Architecture

A running implementation of
[docs/patterns/two-tier-architecture.md](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/two-tier-architecture.md)
and the two documents under it,
[core-services.md](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/core-services.md)
and [orchestrators.md](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/orchestrators.md):
three data-owning **core services** under one process-owning **orchestrator**, where the
multi-service write is a **saga** — and all three of a saga's outcomes are reachable from a `curl`.

## Status

| Piece | .NET | Go | TypeScript | Python |
|---|---|---|---|---|
| Core tier (Tenant, User, Billing) | ✅ | — | — | — |
| Orchestrator + signup saga | ✅ | — | — | — |

## The claim, and how to check it yourself

> **A multi-service write either wholly succeeds or is wholly compensated — and when it can't be,
> it says so rather than pretending.**

Sign up a company. It writes to three services, each with its own store, with no transaction
spanning them:

```bash
cd dotnet
docker compose up --build -d

curl -X POST http://localhost:9280/signups -H 'content-type: application/json' \
  -d '{"company":"acme","email":"admin@acme.com","plan":"standard"}'
```

```json
{"outcome":"Succeeded","tenantId":"tnt-07bc8c02","userId":"usr-d34c7036","accountId":"acc-737d9182"}
```

Now make it fail, three different ways. The request body is the only thing that changes.

### 1. Stage 1 fails — and its *concurrent sibling* is compensated

```bash
curl -i -X POST http://localhost:9280/signups -H 'content-type: application/json' \
  -d '{"company":"badplan","email":"a@b.com","plan":"platinum"}'
```

`HTTP 422`, with the *failing step's own* status carried through, so "we don't sell that plan"
doesn't arrive as "something broke":

```
"detail": "Signup failed at stage 0 and was rolled back cleanly., Nothing was left behind."
```

Tenant and Billing share stage 1 and run **concurrently**, so the tenant may well have been created
before Billing rejected the plan. `curl http://localhost:9281/tenants` — the count is unchanged. A
stage compensated its own succeeded step, not just an earlier stage's.

### 2. Stage 2 fails — both stage-1 effects undone, in reverse

```bash
curl -X POST http://localhost:9280/signups -H 'content-type: application/json' \
  -d '{"company":"baduser","email":"not-an-email","plan":"standard"}'
```

`HTTP 422` again. Tenant *and* billing account counts are both back where they started.

### 3. The rollback itself fails — `PartiallyRolledBack`

A company whose name starts with `sticky` cannot be deleted, so the compensation fails too:

```bash
curl -i -X POST http://localhost:9280/signups -H 'content-type: application/json' \
  -d '{"company":"sticky-corp","email":"not-an-email","plan":"standard"}'
```

`HTTP 500` — deliberately **not** a 422 — and the body says all three things a caller needs:

```
"detail": "Signup failed at stage 1 and rollback did not fully succeed.,
           1 effect(s) may still be applied - reconciliation needed.,
           This outcome is never retried automatically."
```

And it is not a story: `curl http://localhost:9281/tenants` now shows `sticky-corp` still there.

**This distinction is the point of the whole example.** A clean `RolledBack` is safe to retry: the
system is exactly as it was. A `PartiallyRolledBack` is not, and retrying on top of a
possibly-applied effect is how you double-charge a customer. Both are "the request failed"; only one
of them is safe to press the button again on. A saga that collapses them into one error is worse
than no saga.

CI asserts all four cases against real service state, not against log lines —
[`smoke-two-tier-dotnet.yml`](../.github/workflows/smoke-two-tier-dotnet.yml).

## What's here

```
dotnet/
  Contracts/               topic strings and DTOs - shared by both tiers, owned by neither
  Core/                    the DATA tier: one aggregate each, own store, no process logic
    TenantService/           tenant:create, tenant:delete
    UserService/             user:create,   user:delete
    BillingService/          billing:setup, billing:teardown
  Orchestrator/            the PROCESS tier: signup:start, and the saga that runs it
```

Read the `Topics` class in `Contracts/Contracts.cs` and the architecture is already visible: `tenant:*`, `user:*` and
`billing:*` are CRUD on one aggregate each; `signup:start` is a business process across all three.
Nothing in the core tier knows signup exists.

## The rules, and where each one shows up

| # | Rule | Where you can see it |
|---|---|---|
| 1 | One database per core service; share nothing | Three `ConcurrentDictionary` stores in three processes. No service reads another's |
| 2 | Dependencies point one way | `CreateUserRequest` carries a `tenantId`; the Tenant service has never heard of users |
| 3 | Core services hold no cross-service process | Every core handler is validate → write one aggregate → return. The only file with process logic is `SignupHandler` |
| 4 | Every multi-service write is a saga | `SignupHandler` — every `Do` paired with a `Compensate`, and the three outcomes above |
| 5 | Address services by topic, not by transport | The saga says `SendAsync("tenant:create", …)`. `Orchestrator/StartUp.cs` is the only file that knows a URL |
| 6 | Every service is a good mesh citizen | Ordinary Benzene services throughout; the descriptor/mesh feeds come for free |

Rule 2 is the one CI can enforce mechanically here, and it does — the tier-direction guard greps for
`AddOutboundRouting` under `Core/` and fails the build if it finds any. **A core service that grew
an outbound route has quietly become an orchestrator**, and that is exactly the drift that turns a
clean two-tier estate into a call graph with cycles in it. The check is three lines and it is worth
more than the paragraph in a design doc that it replaces.

## Stages are dependency; steps are parallelism

The saga's shape is not stylistic:

```csharp
.Stage(stage => stage
    .Step<TenantCreated>(…)            // independent
    .Step<BillingAccountCreated>(…))   // independent - runs concurrently with the tenant
.Stage(stage => stage
    .Step<UserCreated>(step => step
        .Do(ctx => … ctx.Get<TenantCreated>().TenantId …)))   // needs stage 1's output
```

Tenant and Billing need nothing from each other, so they share a stage. The user needs the tenant
id, so it goes in the next one and reads it from the shared context. Put everything in one stage and
you lose the ordering the process actually requires; put everything in its own stage and you have
serialized two calls that had no reason to wait for each other.

## Compensations are ordinary handlers

`DeleteTenantHandler` does not know it is a compensation. To the Tenant service it is a delete, with
the same validation and the same result vocabulary as any other topic. The saga is the orchestration;
the core services just do the work. That is why the core tier stays free of process logic even
though the process depends entirely on it.

## Be honest about what this demo isn't

- **The stores are in-memory.** Restart a container and the estate is empty. The pattern is about
  the seam between the tiers, not about persistence — the [transactional
  outbox](../transactional-outbox/README.md) example is where durability is the subject.
- **The orchestrator is stateless between requests**, as the pattern recommends. Saga state lives
  for the length of one call. A process that must survive an orchestrator crash mid-saga needs
  durable saga state, which this does not have and does not pretend to.
- **The transport is HTTP**, because that runs on a laptop. In the pattern's AWS realization these
  are Lambda-to-Lambda invokes. Only `Orchestrator/StartUp.cs` knows the difference — six routes in
  one file — which is the practical form of rule 5.

## The framework gap this needed, again

`Orchestrator/BenzeneMessageOverHttp.cs` is the ~50-line outbound middleware that POSTs the
BenzeneMessage envelope to another service's `/benzene-message` endpoint.

This is the **fifth** copy of that file in this repo, across four independent patterns — the
real-time-risk coordinator, the modular monolith's extracted Orders service, both halves of the
transactional outbox, and now this orchestrator. `Benzene.Clients.Http` already ships
`HttpBenzeneMessageClient`, but it is registered as an `IBenzeneMessageClient` and there is no
`UseBenzeneMessageOverHttp()` extension on `OutboundContext` to bind it into a route, the way
`UseSqs`/`UseServiceBus`/`UseInProcess` do. Four independent patterns needing the same adapter is about as clear an
argument for closing the gap upstream as this repo can produce.
