# go-tui-dotnet-backend

A **live, interactive terminal UI written in Go (Bubble Tea)** attached to a **.NET 10
backend** over Server-Sent Events — the working example for
[A live TUI in the AI era](https://shaahink.github.io/site/blog/a-live-tui-in-the-ai-era/).
The same split runs in production in [Conductor](https://github.com/shaahink/conductor),
where a Go companion app watches a C# orchestrator.

Two standalone halves:

```
backend/   .NET 10 minimal API — simulates a gated delivery run, streams every state
           change over SSE (TypedResults.ServerSentEvents), takes commands over POST
tui/       Go + Bubble Tea v2 — renders the run live (stage board + event feed) and
           sends pause / resume / restart while the stream keeps flowing
```

The wire between them is deliberately boring: `text/event-stream` one way, JSON POST the
other. No SignalR client, no WebSocket handshake, no shared code — which is exactly why a
Go front end and a C# back end can meet in the middle.

## Build and run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and
[Go 1.26+](https://go.dev/dl/). Each half builds standalone.

Terminal 1 — the backend:

```powershell
cd backend
dotnet run          # listens on http://127.0.0.1:5058
```

Terminal 2 — the TUI:

```powershell
cd tui
go run .            # or: go run . -url http://somewhere-else:5058
```

## What you should see

The TUI paints a stage board on the left (`·` pending, `▶` running, `✗` failed,
`✓` delivered) and a live event feed on the right. The simulated run includes an agent that
lies about its tests and gets caught by the gate battery, then fixed. Keys:

| Key | Action |
|-----|--------|
| `p` | pause the run (the stream stays live — pausing is a *backend* state) |
| `r` | resume |
| `n` | start a new run |
| `q` | quit the TUI — the backend doesn't care |

Kill the TUI and reattach whenever you like: new subscribers get a replay of recent history,
so the screen fills instantly. And because it's just SSE, `curl -N
http://127.0.0.1:5058/run/events` is a perfectly good "TUI" too.
