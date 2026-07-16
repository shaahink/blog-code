# gated-agent-delivery

An autonomous delivery loop for coding agents that **never believes its agents**. It drives a
three-stage plan by spawning one agent session per attempt (a child process emitting the same
stream-JSON as `claude -p --output-format stream-json`), and after every session it verifies
the work independently — the agent's success report is not evidence, the gate battery is.

Everything that goes wrong in real unattended runs goes wrong here, on purpose and in a
deterministic script:

- **stage 1** — the backend refuses with a usage limit → back off and retry, **no attempt burned**
- **stage 2** — the agent *lies* about finishing → the gates catch it, and the fix session gets
  the actual failing gate output embedded in its prompt
- **stage 3** — the agent goes silent mid-task → the watchdog kills it and burns an attempt
- between the runs — a simulated **power cut** → atomic state snapshots make the second run
  resume exactly where the first died

Companion code for
[Gated delivery for a team of agents](https://shaahink.github.io/site/blog/gated-delivery-for-a-team-of-agents/).
The real, unsimplified loop lives in [Conductor](https://github.com/shaahink/conductor).

## What's in it

| File | What it shows |
|------|----------------|
| `AgentSession.cs` | an agent CLI as a supervised child process: `ArgumentList`, raw-stream tee, typed event parsing, `LastActivityUtc` |
| `Watchdog.cs` | the judgement ladder: `Stalled` / `TimedOut` / `LimitBackoff` / `AgentError` / `Completed` — each with a *different* next move |
| `Gates.cs` | the independent gate battery — re-derives the truth from the filesystem |
| `RunState.cs` | crash-safe state: persist every transition, write atomically (tmp + rename) |
| `Orchestrator.cs` | the delivery loop that ties them together |
| `FakeAgent.cs` | the bundled fake agent (`ok` / `lie` / `stall` / `limit`), so nothing needs to be installed |

## Build and run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). No packages,
no agent installed — the demo spawns itself as the fake agent. Runs in about ten seconds.

```powershell
dotnet run
```

## What you should see

Two runs. The first delivers stages 1 and 2 (catching one usage limit and one lie along the
way) and dies to a power cut. The second resumes from the on-disk state, catches a stall on
stage 3, and finishes:

```
  delivered 3/3 stages · sessions: 6 · lies caught by gates: 1 · stalls caught by watchdog: 1
  resumed after crash: True
```

Every session's raw stream is teed to `bin/…/delivery-demo/session-NNN.jsonl` — the forensic
record for when a session goes weird at 2 a.m.
