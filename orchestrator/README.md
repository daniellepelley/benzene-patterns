# Orchestrator Pattern — a polyglot signup saga

A running implementation of
[docs/patterns/orchestrators.md](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/orchestrators.md):
an orchestrator owns a business *process*, drives it across dumb core services, and makes the
multi-service change **atomic** via a saga — total success or total rollback, never half-applied.

**This example is deliberately polyglot: every service is a different language.** That is the
point of it, and it is what the per-language stacks elsewhere in this repo structurally cannot
show — see [Why this one is shaped differently](#why-this-one-is-shaped-differently).

## The process

`signup:start` creates a tenant, then a user inside it. If the user step fails, the tenant is
deleted again, so a failed signup leaves no orphan behind.

```
                        POST signup:start
                               │
                    ┌──────────▼───────────┐
                    │ Signup Orchestrator  │  .NET  (Benzene.Saga)
                    │  stage 1: tenant     │
                    │  stage 2: user       │
                    └──────┬───────┬───────┘
            tenant:create  │       │  user:create
            tenant:delete  │       │  user:delete      (compensations)
                   ┌───────▼──┐ ┌──▼─────────┐
                   │  Tenant  │ │    User    │
                   │    Go    │ │   Python   │
                   └──────────┘ └────────────┘
```

Every arrow is the **same call**: an HTTP POST of the wire envelope to `/benzene/invoke`. The
orchestrator's client code does not vary by callee language — that is the interop claim this
example exists to prove.

## Status

| Service | Language | Topics | State |
|---|---|---|---|
| Tenant | Go | `tenant:create`, `tenant:delete` | ✅ built, compiles against the published module |
| User | Python | `user:create`, `user:delete` | ✅ built, verified end-to-end over `/benzene/invoke` |
| Signup Orchestrator | .NET | `signup:start` | 🚧 **not yet built** — see [Next](#next) |
| Billing | TypeScript | `billing:setup`, `billing:cancel` | ⏳ waiting on npm publish — see [below](#waiting-on-npm-the-typescript-service) |

The two core services are real and independently verified. The orchestrator that ties them
together is the remaining piece, so **there is no `docker compose up` yet** — that lands with it.

### What "verified" means here

Not "it compiles":

- **Tenant (Go)** — `go build` against the real published module
  (`github.com/daniellepelley/benzene-go`, resolved by pseudo-version; the module has no tags),
  `gofmt` clean.
- **User (Python)** — installed from PyPI and booted, then driven over `/benzene/invoke`:
  `user:create` → `created`; the failure trigger → `bad-request` with the RFC-shaped error
  payload; `user:delete` of an unknown id → `ok` (proving the compensation is idempotent);
  `/benzene/health` → the standard aggregate. **Cross-language interop was checked explicitly**:
  a camelCase request body (`tenantId`) deserializes into the snake_case dataclass field, per
  [wire-contracts §6](https://github.com/daniellepelley/Benzene/blob/main/docs/specification/wire-contracts.md)'s
  case-insensitive read rule — which is what makes a .NET or Go caller work without the Python
  service knowing anything about it.

## The contract between services

Everything crossing a process boundary here is the spec's wire envelope — no bespoke REST shapes,
which is why a caller needs no per-callee knowledge:

```jsonc
// POST http://tenant:8080/benzene/invoke
{ "topic": "tenant:create", "headers": {}, "body": "{\"companyName\":\"Acme\"}" }

// 200 OK - note statusCode is the *Benzene* status, not HTTP
{ "statusCode": "created", "headers": {"content-type":"application/json"},
  "body": "{\"tenantId\":\"tenant-1\",\"companyName\":\"Acme\"}" }
```

`body` is a **pre-serialized string**, not an inline object — the envelope schema stays fixed
whatever the payload schema is. A failed result carries the problem-details-shaped error payload
(`{"status":"conflict","detail":"..."}`) as its `body`.

| Topic | Request | Success | Failure the saga must handle |
|---|---|---|---|
| `tenant:create` | `{companyName}` | `created` `{tenantId, companyName}` | `conflict` — name already taken |
| `tenant:delete` | `{tenantId}` | `ok` `{tenantId, deleted}` | — idempotent, never fails |
| `user:create` | `{tenantId, email}` | `created` `{userId, tenantId, email}` | `conflict` — email taken; `bad-request` — see trigger below |
| `user:delete` | `{userId}` | `ok` `{userId, deleted}` | — idempotent, never fails |

**Compensations are idempotent on purpose.** Deleting an already-deleted (or never-created)
entity succeeds rather than 404-ing, because a compensation may run after a partial failure and
may be retried. A compensation that could itself fail would downgrade a clean `RolledBack` into
a `PartiallyRolledBack` — the one outcome the saga's invariant cannot restore.

### Driving the rollback path

`user:create` rejects any address ending `@fail.example` with `bad-request`. That is the demo's
deliberate, side-effect-free way to make stage 2 fail so stage 1's `tenant:delete` compensation
runs — no need to first exhaust a real conflict.

## Why this one is shaped differently

The repo's other pattern ([real-time-risk](../real-time-risk/README.md)) follows the default
convention: **one stack per language**, the same system built four times, proving each port can
express it. This example is the other shape — **one stack, many languages** — because the thing
it demonstrates only exists between languages: a .NET orchestrator running a saga whose forward
actions and compensations land on a Go service and a Python service, over one wire contract, with
no language-specific client code. Building it four times would demonstrate the opposite of its
point. See the root [README's Conventions](../README.md#conventions) for how both shapes coexist.

## Waiting on npm: the TypeScript service

Billing was to be the third core service, in TypeScript, giving the saga a three-stage shape with
an irreversible-effect stage last. It is deferred **only until the TypeScript port finishes
publishing** — no design decision is outstanding.

This repo's convention is that each implementation consumes the **published** packages.
benzene-typescript publishes under **`@benzenejs/*`**, a scope the project owns, and the rename to
it is already complete in that repo. Publishing is simply partway through: as of 2026-08-14,
**25 of its 129 packages are on npm** — the run reached the `a*` range (`abstractions`, `ajv`,
`auth-*`, `avro`, `aws-*`) and stopped on rate limiting. The packages a core service needs are in
the pending set:

| Package | Status (2026-08-14) |
|---|---|
| `@benzenejs/abstractions`, `@benzenejs/abstractions-message-handlers` | published `0.1.0-beta.1` |
| `@benzenejs/core`, `@benzenejs/results`, `@benzenejs/core-message-handlers` | pending |
| `@benzenejs/http`, `@benzenejs/express` | pending |

So this one clears itself: re-run the publish, and the billing service becomes ordinary work.

> Note for anyone searching npm directly: the unscoped **`@benzene`** scope is a *different,
> unrelated project* (a GraphQL server by `hoangvvo`, holding `@benzene/core` and `@benzene/http`).
> Benzene-the-framework is `@benzenejs/*`. Don't install from `@benzene/*`.

## Next

1. **Signup Orchestrator (.NET)** — the remaining piece. `Benzene.Saga` is the engine the pattern
   doc's own example uses. **Pin every Benzene package to `0.0.2-alpha.6`** (or to `0.0.3-alpha.1`
   or later once a release after 2026-08-14 exists), and do **not** use `--prerelease`:

   benzene-dotnet's July 2026 switch from a four-part version scheme to dotted prereleases reused
   base `0.0.2`, so `0.0.2.18-alpha` (published 2026-07-14) sorts *above* `0.0.2-alpha.6`
   (published 2026-08-13) even though the latter is a month newer. Latest-prerelease resolution
   therefore picked the July build for packages carrying both schemes, while `Benzene.Saga` and
   `Benzene.Clients.Http` — added after the switch — only ever had `0.0.2-alpha.*`. Taking "latest
   prerelease" of each gets two generations that were never built together, silently. Fixed
   upstream by bumping the base to `0.0.3`
   ([benzene-dotnet `6afaef0`](https://github.com/daniellepelley/benzene-dotnet/commit/6afaef0)),
   so any release after that resolves correctly; exact pins remain the safe choice regardless.
2. **`docker-compose.yml`** — lands with the orchestrator, since a stack of two callee services
   and no caller has nothing to demonstrate.
3. **Billing (TypeScript)** — once the pending `@benzenejs/*` packages above are published.
4. **A black-box test** driving `signup:start` twice (happy path, then the `@fail.example`
   rollback) and asserting the tenant is gone after the failure — the assertion that actually
   proves the saga's invariant rather than claiming it.
