# Boilerplate these examples can drop once the next alpha ships

These examples pin `0.0.3-alpha.2`. Changes landed in benzene-dotnet **after** that release let
several more lines go — recorded here so the deletion happens on the version bump rather than being
rediscovered.

## `GetConfiguration` — 20 copies

`BenzeneStartUp.GetConfiguration` is now virtual, defaulting to
`new ConfigurationBuilder().AddEnvironmentVariables().Build()`.

Of the 25 StartUps here that override it, **20 have exactly that body** and can simply delete the
override. The other 5 build a richer configuration (a base path, extra sources) and correctly keep
theirs — which is the point of the change: the override is for a steer, not for the default.

```csharp
// Delete, in 20 files:
public override IConfiguration GetConfiguration()
    => new ConfigurationBuilder().AddEnvironmentVariables().Build();
```

## What is NOT affected

Checked, so the bump is not a surprise:

- No example uses `MeshServiceDescriptor.Consumes` or `ICloudServiceBuilder.WithConsumes`, both
  renamed by the 2026-08 mesh role inversion. Nothing here reads the mesh descriptor at all.
- The role inversion is a **wire** change (`consumes` → `produces`, and `topics` now meaning what a
  service consumes). No example in this repo runs a collector or asserts a mesh graph, so nothing
  here encodes the old roles.
- `MeshAggregationPass` is new public surface with no existing caller here.

## Verification to redo on the bump

The five patterns whose behaviour was checked end-to-end against alpha.2, which should be re-run
rather than assumed:

- two-tier: all four saga outcomes, including the 422-vs-500 split and the orphan it leaves
- modular-monolith: same order in-process and over HTTP, plus the compensation path
- choreography: one emit, three reactions, one correlation id
- cqrs: the read model joining two write services
- streaming: resume-from-failure checkpointing and the idempotent fold
