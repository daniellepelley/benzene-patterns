# Release runbook — the steps this repo's CI sandbox can't do

Building the ports surfaced that the authoring environment (Claude Code on the web) **cannot publish
anything**: it has no npm token, and creating a git **tag** is denied both over git (`403`) and over
the GitHub REST API ("write not permitted through this proxy"). So the ports consume their frameworks
as best they can today (Go via the module-proxy pseudo-version; Python and TypeScript from source), and
the actual releases must be cut from an **unrestricted environment — your laptop or the language repos'
own CI** (which is not behind this proxy). This runbook is exactly those steps.

Do these in the framework repos (`benzene-go`, `benzene-python`, `benzene-typescript`), then flip each
port from source-consumption to the published package with the one-line change noted per language.

---

## 1. Go — push a tag (that's the whole "publish")

The Go port already consumes a genuinely-resolvable version (the proxy pseudo-version), so this is an
*upgrade to a clean semver*, not an unblock. In a checkout of `benzene-go`:

```bash
git tag -a v0.1.0 -m "v0.1.0"
git push origin v0.1.0
```

(Root module only is needed for this pattern's slice. Nested modules — `awssqs`, `grpcbinding`, … —
get their own `subdir/v0.1.0` tags per `benzene-go/RELEASING.md`, but the real-time-risk slice uses
only the root module.)

Then in this repo, switch `real-time-risk/go/go.mod` from the pseudo-version to `v0.1.0` (there's a
`// TODO: switch to v0.1.0 once tagged` next to the require) and run `go mod tidy`.

## 2. Python — push a tag; OIDC publishes to PyPI

`benzene-python/.github/workflows/release.yml` publishes every `packages/*` distribution to PyPI via
trusted publishing (OIDC) on any `v*` tag. **Prerequisite:** each distribution (`benzene-core`,
`benzene-aws`, …) must have a **trusted publisher** (or a *pending publisher*) configured on PyPI for
that repo+workflow, or the publish step 403s. Once that's set:

```bash
git tag -a v0.0.1 -m "v0.0.1"
git push origin v0.0.1
```

Then in this repo, switch `real-time-risk/python/requirements.txt` from the
`… @ git+https://…@b073c95#subdirectory=packages/<dist>` lines to plain `benzene-core==0.0.1`
(etc.), and rebuild the images.

## 3. TypeScript — a new npm scope, then publish

`@benzene/*` on npm is owned by an **unrelated** project (`hoangvvo/benzene`, a GraphQL library), so
the port targets the scope **`@benzenejs`** (your choice). Steps in `benzene-typescript`:

1. Create/own the `@benzenejs` scope on npm (an org or user scope you control).
2. Rename the packages `@benzene/*` → `@benzenejs/*` (package.json `name`, and every internal import),
   or publish them under the new scope by whatever mapping you prefer.
3. Add a build + publish workflow (the repo currently ships raw `.ts` and has **no** publish workflow):
   compile to JS + `.d.ts`, then `npm publish --access public` — ideally via **npm OIDC trusted
   publishing** from GitHub Actions (no long-lived token), or an `NPM_TOKEN` repo secret.
4. Cut a version tag to trigger it.

Then in this repo, switch `real-time-risk/typescript/` from its source-consumption setup (tsconfig
`paths` / bundled framework source) to `@benzenejs/*` dependencies in `package.json`. The port's
`PARITY-NOTES.md` marks every import that changes.

---

## 4. Clean up the stray probe branch on `benzene-go`

Diagnosing the tag-push block, this session created a throwaway branch on `benzene-go` and the proxy
then refused to delete it (ref deletion is blocked too). It is harmless — it points at the same commit
as `main` — but please delete it:

```bash
git push origin --delete ccr-pushtest-delete-me
# or on GitHub: Branches → delete ccr-pushtest-delete-me
```

## 5. Run the CI

All the workflows in this repo trigger on push/PR to **`main`** (mirroring the pre-existing .NET smoke
workflow), so nothing has run on the feature branch yet. Opening a PR from
`claude/live-trading-multi-lang-parity-h7t0zy` to `main` will run, for the first time end-to-end:

- `smoke-real-time-risk-{dotnet,go,python,typescript}.yml` — each language's compose stack, booked
  trade → projection.
- `parity-real-time-risk.yml` — the one shared black-box suite across every language.
- `terraform-validate.yml` — provider-backed `terraform validate` (blocked in the sandbox).
- `build-lambda-images.yml` — `docker build` of every Lambda image.

These are the real end-to-end checks; locally in the sandbox only builds + unit tests + `fmt` ran (no
docker daemon, no cloud, no Terraform registry).
