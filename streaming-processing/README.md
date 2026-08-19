# Real-Time Stream Processing

A running implementation of
[docs/patterns/streaming-processing.md](https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/streaming-processing.md):
a market-data tick pipeline that rolls a firehose into one-minute OHLC bars — **one pipeline
invocation per batch**, ordered, windowed, checkpointed, and correct after a mid-batch failure.

The stream binding is the one Benzene shape that is *not* "a message arrives, a handler runs". Every
other example in this repo fans a batch **out** into independent per-message invocations. This one
fans it **in**, and everything interesting follows from that.

## Status

| Piece | .NET | Go | TypeScript | Python |
|---|---|---|---|---|
| Fan-in stream pipeline, `Window` / `PartitionBy` | ✅ | — | — | — |
| Monotonic checkpoint, resume-from-failure | ✅ | — | — | — |
| Idempotent rolling aggregation (and the bug it fixes) | ✅ | — | — | — |

## The claim, and how to check it yourself

> **Delivery is at-least-once with resume-from-sequence, so a rolling aggregation is either an
> idempotent fold or quietly wrong.**

```bash
cd dotnet
docker compose up --build -d
B=http://localhost:9680

curl -X POST $B/ticks -H 'content-type: application/json' -d '{"ticks":[
  {"symbol":"NVDA","price":10,"size":1, "minute":"09:32"},
  {"symbol":"NVDA","price":11,"size":2, "minute":"09:32"},
  {"symbol":"NVDA","price":12,"size":4, "minute":"09:32"},
  {"symbol":"NVDA","price":13,"size":8, "minute":"09:32"},
  {"symbol":"NVDA","price":14,"size":16,"minute":"09:32"},
  {"symbol":"NVDA","price":15,"size":32,"minute":"09:32"}]}'
```

Rig the **fifth** record to fail once, then drain in windows of three:

```bash
curl -X POST $B/poison -H 'content-type: application/json' -d '{"sequenceNumber":11,"times":1}'
curl -X POST $B/drain  -H 'content-type: application/json' -d '{"windowSize":3}'
```

```json
{"recordsRead":6,"recordsApplied":4,"checkpointBefore":6,"checkpointAfter":9,
 "failed":true,"failureReason":"Record at sequence 11 failed to process."}
```

Read that carefully — three separate things happened:

- **The first window was checkpointed.** `checkpointAfter` is 9, not 6. Progress survived the failure.
- **The checkpoint stopped short.** It did not reach 12. A checkpoint that advanced past a record
  that never processed would lose it silently, which is the one thing a stream must never do.
- **Record 10 was applied and record 11 was not.** The batch died mid-window.

Drain again:

```bash
curl -X POST $B/drain -H 'content-type: application/json' -d '{"windowSize":3}'
# {"recordsRead":3,"recordsApplied":3,"checkpointBefore":9,"checkpointAfter":12,"failed":false}
```

**Three records, not six** — resume from the failure, not from zero. But record 10 is among them, and
it was already folded in. So:

```bash
curl $B/bars
# {"symbol":"NVDA","minute":"09:32","open":10,"high":15,"low":10,"close":15,
#  "volume":63,"sequences":[7,8,9,10,11,12]}
```

`1+2+4+8+16+32 = 63`. The replayed record was counted **once**.

### And now the same run with the obvious fold

`BarStore.ApplyNaive` does what everyone writes first — `bar.Volume += tick.Size` — and the example
keeps it runnable behind a flag:

```bash
curl -X POST $B/drain -H 'content-type: application/json' -d '{"windowSize":3,"naive":true}'
```

Same ticks, same failure, same recovery. Volume comes out **71**. Eight units of volume that never
traded, in a number somebody prices off, with nothing anywhere reporting a problem — the pipeline
succeeded, the checkpoint advanced, the bar exists. This is the failure mode the pattern's
"aggregation must be idempotent or replayable" line is about, and it is the reason it is worth
running rather than reading.

CI asserts both numbers: 63 for the fold, **71 for the bug**. If a future change accidentally fixed
the naive path, the test fails — a demo of a bug has to keep reproducing the bug.

## `Window` and `PartitionBy` are not interchangeable

Both ship in `Benzene.Core.Middleware` and both group a batch. The difference decides whether you get
partial progress, so the example implements both and lets you switch:

```bash
curl -X POST $B/drain -H 'content-type: application/json' -d '{"mode":"partition"}'
# {"recordsRead":6,"recordsApplied":2,"checkpointBefore":18,"checkpointAfter":18,"failed":true}
```

`checkpointAfter == checkpointBefore` — **no progress at all**, and the next drain re-reads all six.

| | `Window(n)` | `PartitionBy(key)` |
|---|---|---|
| Memory | lazy — yields each window as it fills | **buffers the whole batch** to group it |
| Order | shard order preserved | per-key order preserved; keys in first-seen order |
| Checkpoint | after each window — **partial progress survives** | end of batch only — a failure replays everything |
| Reach for it when | batching store round-trips (the default) | the computation needs all of a key's records together |

Neither is better. But `PartitionBy` costs partial progress *and* bounded memory, and it is the one
the pattern doc's snippet shows — so it is worth knowing what you are buying before copying it. The
example's default is `Window`.

## Rolling state lives in a store, not in memory

One invocation sees one batch. A one-minute bar routinely spans several, so it cannot live in a local
variable between them — `BarStore` is keyed by `(symbol, minute)` and carries the bar across
invocations. `Window` and `PartitionBy` order and group **within** a batch; the store is what carries
state **between** them.

The pattern doc calls this "the one thing newcomers get wrong about serverless streaming", and the
smoke test pins it: the NVDA bar above is built from two separate drains and comes out right.

## What's here

```
dotnet/TickPipeline/
  Shard.cs             a local stand-in for one Kinesis shard: ordered, sequence-addressed, checkpointed
  Bars.cs              the OHLC bars, the idempotent fold, and the naive one kept runnable
  StreamProcessor.cs   builds the Benzene stream pipeline and runs one batch through it
  Handlers.cs          publish ticks, drain a batch, inspect bars and the shard, rig a failure
```

## What is real, and what is local

**Real, and Benzene's own** — all from `Benzene.Core.Middleware`, all transport-neutral:
`StreamContext<T>`, the `UseStream` step, `Window(n)`, `PartitionBy(key)`, `IStreamCheckpointer<T>`.
Under `UseKinesisStream` or `UseEventHubStream` the handler body is unchanged; what changes is who
hands it the batch. CI greps for each of these by name, so an edit that quietly reimplemented them
locally would fail the build rather than pass the behavioural tests while proving nothing.

**Local**: `Shard`, and the `POST /drain` endpoint that drives one invocation. A shard has to provide
exactly three properties, and this provides all three — ordered records addressed by a monotonic
sequence number, batches read from the checkpoint forward, and a checkpoint that only ever advances.
Driving it from an endpoint rather than a poll loop is what makes the checkpoint observable one batch
at a time, which is the only way to show "resume from the failure" as something other than a claim.

`POST /poison` is the other demo affordance. At-least-once redelivery is the condition under which a
rolling aggregation is either right or quietly wrong, and it is not something you can wait for.

## A one-word framework finding

`StreamExtensions.UseStream` is documented as *"a terminal stream-processing step"* — and it is,
nothing runs after it. But it was built on the ordinary `Use(name, func)` rather than
`UseTerminal(name, func)`, so it was not marked `ITerminalMiddleware`, and Benzene's own start-up
check refused to boot a pipeline that ended in it:

```
terminal-middleware: 1 pipeline(s) cannot handle a message:
  the StreamContext`1 pipeline has no terminal middleware
```

That was the check doing exactly its job, on a false positive. This example carried a local
`TerminalStream.cs` — the same step built on `UseTerminal` — until **0.0.3-alpha.2 made that one-word
change upstream**; the shipped `UseStream` is now what this pipeline calls, and the local copy is
deleted.

Worth noting how it was found: not by reading the source, but by the start-up check failing on the
first run and naming both the pipeline and the missing piece — the same check that caught a missing
terminal middleware in three other examples in this repo.

## Be honest about what this demo isn't

- **The shard is in-memory and single.** Restart the container and the log is gone. Real parallelism
  is shard count (AWS) or `ConcurrentRequests` (self-hosted workers); one shard keeps the ordering
  assertions deterministic.
- **The bars are in-memory.** In a real pipeline this store is DynamoDB or Redis, one item per
  `(symbol, minute)` — which is the point being made about cross-invocation state, not about the
  database.
- **Batches are pulled by an HTTP call, not by a poller.** That is the whole of what a real transport
  would replace, and it is deliberate: a poller would make the checkpoint impossible to watch.
- **Nothing publishes `bar:closed`.** A closed bar becomes an ordinary event, and from there it is
  [choreography](../choreography/README.md) and [read models](../cqrs-read-models/README.md) — both
  of which have their own examples rather than being re-demonstrated here.
