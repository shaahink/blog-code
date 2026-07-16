# 06 — crash-safe state

A worker that persists its full state atomically (tmp + rename) on every transition and
models owed work as data. Run 1 is killed at step 4; run 2 resumes from step 5 and honours
the pending obligation. Corrupt state files are quarantined, never fatal.

```powershell
dotnet run --project 06-crash-safe-state
```

- Post: [Crash-safe orchestration with one JSON file](https://shaahink.github.io/site/blog/crash-safe-with-one-json-file/)
- Real source: [RunState.cs](https://github.com/shaahink/conductor/blob/master/src/Conductor/Models/RunState.cs) in [Conductor](https://github.com/shaahink/conductor)
