# blog-code

[![build](https://github.com/shaahink/blog-code/actions/workflows/build.yml/badge.svg)](https://github.com/shaahink/blog-code/actions/workflows/build.yml)

Runnable companion code for the posts at **[shaahink.github.io/site](https://shaahink.github.io/site/blog/)**.

Each folder is a small, self-contained .NET 10 console project that distils one idea from the
three systems the posts are drawn from:

- **[Shamshir](https://github.com/shaahink/Shamshir)** — a trading engine whose every decision can be replayed, byte for byte
- **[Conductor](https://github.com/shaahink/conductor)** — an orchestrator that runs coding agents unattended, and never believes them
- **[DevContext](https://github.com/shaahink/DevContext2)** — a .NET code graph where every edge cites file and line, with an MCP server

The samples are deliberately simplified — no error handling ceremony, no config, one file each.
Every folder's README links back to the real, unsimplified source.

## Index

| # | Sample | Post | Drawn from |
|---|--------|------|------------|
| 01 | [deterministic-kernel](01-deterministic-kernel/) | [Designing a kernel: one queue, one reducer, one journal](https://shaahink.github.io/site/blog/designing-a-kernel/) | Shamshir |
| 02 | [zeromq-lock-step](02-zeromq-lock-step/) | [A deterministic bridge out of cTrader](https://shaahink.github.io/site/blog/a-deterministic-bridge-out-of-ctrader/) | Shamshir |
| 03 | [mcp-live-session](03-mcp-live-session/) | [A live MCP session in .NET](https://shaahink.github.io/site/blog/a-live-mcp-session-in-dotnet/) | DevContext |
| 04 | [cli-agent-driver](04-cli-agent-driver/) | [Your coding agent has a CLI — drive it like a process](https://shaahink.github.io/site/blog/drive-the-agent-like-a-process/) | Conductor |
| 05 | [agent-watchdog](05-agent-watchdog/) | [Watchdogging an autonomous agent](https://shaahink.github.io/site/blog/watchdogging-an-autonomous-agent/) | Conductor |
| 06 | [crash-safe-state](06-crash-safe-state/) | [Crash-safe orchestration with one JSON file](https://shaahink.github.io/site/blog/crash-safe-with-one-json-file/) | Conductor |
| 07 | [error-envelopes](07-error-envelopes/) | [Fail in 80 tokens: error design for agent-facing tools](https://shaahink.github.io/site/blog/fail-in-80-tokens/) | DevContext |
| 08 | [context-packs](08-context-packs/) | [A context pack is a knapsack problem](https://shaahink.github.io/site/blog/a-context-pack-is-a-knapsack/) | DevContext |
| 09 | [market-data-tape](09-market-data-tape/) | [Record the world once: market-data tapes](https://shaahink.github.io/site/blog/record-the-world-once/) | Shamshir |
| 10 | [simulation-tier](10-simulation-tier/) | [CI for a trading engine, no broker account required](https://shaahink.github.io/site/blog/ci-without-a-broker-account/) | Shamshir |

## Running

```powershell
dotnet build                                   # builds everything
dotnet run --project 01-deterministic-kernel   # each sample runs standalone
```

Requires the .NET 10 SDK. Every sample targets `net10.0`; retargeting to `net9.0` in
`Directory.Build.props` also works — nothing here uses .NET-10-only APIs.
