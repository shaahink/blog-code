# netmq-event-pipeline

Event-driven inter-process messaging in .NET with [NetMQ](https://github.com/zeromq/netmq)
(the pure-C# port of ZeroMQ). Four demos, each one socket pattern and the production lesson
that comes bolted to it:

| Demo | Pattern | The lesson |
|------|---------|------------|
| `pipeline` | PUSH/PULL | fan-out/fan-in with backpressure; measured throughput at 16 B and 1 KB payloads |
| `slowjoiner` | PUB/SUB | subscriptions are messages too — a naive start loses the head of the stream |
| `hwm` | PUB/SUB | the high-water mark drops messages **silently** when a subscriber is slow |
| `lockstep` | DEALER/ROUTER | a sequence-acked protocol that makes two async processes deterministic |

Everything runs in a single process (threads standing in for processes) over real TCP on
localhost, so one `dotnet run` shows the whole story — but the sockets don't know the
difference, and each role works unchanged as a separate process or machine.

Companion code for
[Fast event-driven IPC in .NET](https://shaahink.github.io/site/blog/fast-event-driven-ipc-in-dotnet/).

## Build and run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). NetMQ is the
only package reference; the project is fully standalone.

```powershell
dotnet run                  # all four demos, ~15 s
dotnet run -- pipeline      # or one at a time: pipeline | slowjoiner | hwm | lockstep
```

## What you should see

```
── PUSH/PULL pipeline — fan out, fan in, measure ─────
payload    16 B : 200,000 / 200,000 msgs through 2 hops in 0.77 s — 259,761 msg/s (4 MB/s)
payload 1,024 B : 200,000 / 200,000 msgs through 2 hops in 5.62 s — 35,566 msg/s (35 MB/s)

── PUB/SUB — the slow joiner ─────
naive start    : received    0 of 1,000 events — the head of the stream is simply gone
handshake first: received 1,000 of 1,000 events

── PUB/SUB — the high-water mark drops, silently ─────
published      : 50,000 frames of 128 B
received       : 2,000
dropped        : 48,000 — silently, at the high-water mark

── DEALER/ROUTER — lock-step: determinism over sockets ─────
[engine] venue connected
[venue ] bar   2 -> executing open @ 1.10142
[venue ] bar   4 -> executing close @ 1.10067
...
[engine] 40 bars, 3 entries, 5 executions — done
```

Throughput numbers are from one laptop and unscientific by design — run it on yours. The
*shape* of the result is the point: small messages are message-rate bound, bigger ones become
bandwidth bound, and the pattern (not the transport) decides what happens under pressure.
