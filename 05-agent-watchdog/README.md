# 05 — agent watchdog

Supervises three misbehaving child processes and reduces each to an explicit outcome:
`Stalled` (silence → kill + resume, attempt burned), `TimedOut`, `LimitBackoff` (backend
refusal → wait + resume, **no** attempt burned), `AgentError`, `Completed` (which still
gets verified independently).

```powershell
dotnet run --project 05-agent-watchdog
```

- Post: [Watchdogging an autonomous agent](https://shaahink.github.io/site/blog/watchdogging-an-autonomous-agent/)
- Real source: [Orchestrator.cs](https://github.com/shaahink/conductor/blob/master/src/Conductor/Core/Orchestrator.cs) in [Conductor](https://github.com/shaahink/conductor)
