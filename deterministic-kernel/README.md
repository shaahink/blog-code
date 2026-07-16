# deterministic-kernel

The core of an event-driven trading engine as **one queue, one pure reducer, one journal,
one effect executor** — and the proof that this shape buys you determinism: the demo records
a market-data tape to disk, replays it twice, and shows the two journals are byte-identical
(SHA-256 over every step record). A third run replays the same tape under a different config,
which is a whole new backtest with zero new data plumbing.

Companion code for
[Designing a deterministic kernel](https://shaahink.github.io/site/blog/designing-a-deterministic-kernel/).

## What's in it

- a **pure reducer** — `Kernel.Decide(config, state, event) → (state', effects[])`; no I/O,
  no clock, no randomness inside
- the **funnel driver** — drains each tape event *and all the feedback it triggers* before
  pulling the next one, so there is exactly one total order of events
- an **effect executor** (`Venue`) — the only place I/O would live; fills re-enter the queue
  as events
- a **journal** — one lossless `StepRecord` per processed event
- a **tape on disk** — NDJSON shards, deduped at capture, order-checked on read

Everything is deliberately simplified: no error-handling ceremony, no real broker, one small
strategy whose only job is to produce effects.

## Build and run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). No other
dependencies — the project is fully standalone.

```powershell
dotnet run
```

## What you should see

```
recorded 300 bars (43 duplicate events dropped at capture)
journal entries : 301
replay 1 sha256 : 89F7787232D7CE37
replay 2 sha256 : 89F7787232D7CE37
byte-identical  : True
window=50 run   : 13D82861487F8F14   (different journal, as expected)
```

(Hashes will differ from the above only if you change the code — that's the point.)
