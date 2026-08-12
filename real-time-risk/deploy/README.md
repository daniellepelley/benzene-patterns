# Deploying the Real-Time Risk platform to AWS — one shared stack, every language

This is the "delivered on AWS" half of the pattern, and the place its central claim gets tested: **the
same infrastructure, deployed the same way, for every Benzene language port — only the compiled
artifact changes.** The Terraform in [`terraform/`](terraform/) is not one-stack-per-language; it is
**one stack, parameterised by a map of container-image URIs.** A .NET deploy and a Go deploy run
identical Terraform and differ only in which image each Lambda pulls.

## Why container-image Lambdas (and not zip)

A zip-packaged Lambda forces the language into Terraform: you must set `runtime = "dotnet8"` vs
`"nodejs20.x"` vs `"python3.12"` vs `"provided.al2023"`, and a `handler` string whose format differs
per language. That is the language leaking into the infrastructure — the opposite of what this
exercise is trying to prove.

A **container-image Lambda is just an `image_uri`.** `package_type = "Image"`; no `runtime`, no
`handler`, no `filename` anywhere in the stack (grep the `.tf` files — there are none). The runtime
lives *inside* the image, built from each language's own Dockerfile. So the per-language surface
collapses to exactly one input:

```hcl
service_images = {
  "trade-ledger"     = "<ecr>/rtr-trade-ledger:<language>-<tag>"
  "risk-read-models" = "<ecr>/rtr-risk-read-models:<language>-<tag>"
}
```

Everything else — the DynamoDB event-store table and its stream, the event-source mapping that drives
the CQRS projection, the HTTP API and its two routes, IAM, (optionally) the Kinesis stream and
EventBridge choreography bus — is **byte-for-byte identical across languages.** That identity *is* the
parity test: if a language's image can't satisfy this fixed contract, that's a real gap, surfaced by
deployment rather than argued in a doc.

## Service → deployment mapping

| Service | AWS shape in this stack | Notes |
|---|---|---|
| Trade Ledger | Lambda (container) behind API Gateway `POST /trades` | Writes events to the DynamoDB table. |
| Risk Read Models | Lambda (container): DynamoDB-stream event-source mapping **+** API Gateway `GET /books/{book}/positions` | One function, two triggers — for .NET/TS/Python. |
| Market-Data Aggregator | Lambda (container) with a Kinesis event-source mapping | `enable_market_data`; off until a port ships it. |
| Valuation Service | Lambda (container) on an EventBridge `bar:closed` rule | `enable_market_data`. |
| Risk Coordinator | Lambda (container) on an EOD schedule, fans out Lambda-to-Lambda | `enable_risk_coordinator`. |
| **Pricing Service** | **Not in this stack** | gRPC streaming needs a persistent HTTP/2 listener — not Lambda-shaped in *any* port. Deploys as a container service (ECS/App Runner). See [`../PARITY-FINDINGS.md`](../PARITY-FINDINGS.md) §3.3. |

Two deliberate accommodations, both language-driven, both absorbed by the *inputs* (never the resource
shapes):

- **Go is one-trigger-per-binary** (`awslambda.Start` takes a single handler). Where .NET/TS/Python
  host the Risk Read Models stream trigger and HTTP query in one function, Go supplies two images and
  the stack points the stream mapping and the API route at the respective functions. The table/stream/
  API wiring is unchanged.
- **Event sourcing is app-local outside .NET.** The table shape here (`pk`/`version` + `eventType`/
  `payload`/`timestamp`) matches `Benzene.EventSourcing.DynamoDb`; the other ports write that same
  shape by hand. The infra can't tell the difference, which is the point.

## Deploy flow

Prerequisites: an AWS account, an ECR repo per service, Terraform ≥ 1.6, and the language's image built
and pushed (see each `<language>/` folder's Dockerfile).

```bash
cd terraform
terraform workspace new dotnet          # isolate each language's stack in its own workspace/state
terraform apply -var-file=dotnet.tfvars # dotnet.tfvars = region + language + service_images map
```

Swap `dotnet` for `typescript` / `python` / `go`; the *only* file that differs is the `service_images`
values in the tfvars. Same `terraform apply`, same resources.

## A shared black-box test proves parity

Because every language exposes the identical HTTP contract (`POST /trades`,
`GET /books/{book}/positions`) and the identical eventual-consistency semantics (the response's
`projectedThroughVersion` tells you when the projection has caught up), one language-agnostic test
suite runs against any deployment — local docker-compose *or* a deployed API URL. Same requests, same
assertions, regardless of the language under the hood. That suite (planned, per the root README's
roadmap item 5) is what turns "the same system in every language" from a claim into a check.

## Verification status (honest)

- `terraform fmt` — clean (CI enforces `-check`).
- `terraform validate` — **not run in the authoring sandbox**: this environment's egress policy blocks
  `registry.terraform.io`, so the AWS provider can't be downloaded here. The stack is HCL-parse-clean
  (fmt parses) and hand-reviewed; a CI job runs `terraform init` + `validate` where the registry is
  reachable, and is the source of truth for validation.
- `terraform apply` — requires a real AWS account; not exercised by CI (no credentials in this repo).
  The stack is written to be applied by a user with their own account, exactly like the local
  docker-compose stacks need no cloud account.
