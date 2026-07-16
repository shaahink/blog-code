# 04 — CLI agent driver

Runs a headless coding agent as a supervised child process: redirected pipes, templated
arguments, stream-json parsed into typed events, raw stream teed to a log, last-activity
tracked for the watchdog. Demo spawns a built-in fake agent, so it needs nothing installed.

```powershell
dotnet run --project 04-cli-agent-driver
```

- Post: [Your coding agent has a CLI — drive it like a process](https://shaahink.github.io/site/blog/drive-the-agent-like-a-process/)
- Real source: [AgentSession.cs](https://github.com/shaahink/conductor/blob/master/src/Conductor/Core/AgentSession.cs), [JobObject.cs](https://github.com/shaahink/conductor/blob/master/src/Conductor/Core/JobObject.cs) in [Conductor](https://github.com/shaahink/conductor)
