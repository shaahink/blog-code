// The delivery loop. One stage at a time, one session per attempt, and after every session
// the same ritual: run the gates, read the world, believe nothing else.

public sealed record Stage(string Id, string Goal, string Artifact, string MustContain);

public static class Orchestrator
{
    public static string WorkDir => Path.Combine(AppContext.BaseDirectory, "delivery-demo");

    private static readonly Stage[] Plan =
    [
        new("S1", "produce the API sketch", "stage-1.txt", "deliverable 1"),
        new("S2", "produce the data model", "stage-2.txt", "deliverable 2"),
        new("S3", "produce the test plan", "stage-3.txt", "deliverable 3"),
    ];

    // The demo's script: which behaviour the fake agent exhibits on each spawn of a stage.
    // In production this column is life; here it's deterministic so the run is repeatable.
    private static string Behaviour(string stageId, int spawn) => (stageId, spawn) switch
    {
        ("S1", 1) => "limit", // the backend refuses — not the agent's fault
        ("S2", 1) => "lie",   // claims success, does nothing — the reason gates exist
        ("S3", 1) => "stall", // goes silent mid-task — the reason watchdogs exist
        _ => "ok",
    };

    private const int MaxAttempts = 3;
    private static readonly Limits SessionLimits = new(StallSeconds: 2.0, TimeoutSeconds: 20.0);

    public static void Deliver(int crashAfterStage)
    {
        Directory.CreateDirectory(WorkDir);
        var statePath = Path.Combine(WorkDir, "state.json");
        var state = RunState.LoadOrNew(statePath);

        if (state.CompletedStages.Count > 0)
        {
            state.ResumedAfterCrash = true;
            Console.WriteLine($"  resume: {state.CompletedStages.Count} stage(s) already checkpointed" +
                              $" ({string.Join(", ", state.CompletedStages)}) — continuing from there\n");
        }

        for (var i = 0; i < Plan.Length; i++)
        {
            var stage = Plan[i];
            if (state.CompletedStages.Contains(stage.Id)) continue;

            var attempt = 1;
            var spawn = 0;
            string? fixEvidence = null;

            while (attempt <= MaxAttempts)
            {
                spawn++;
                state.SessionsSpawned++;
                state.Save(statePath);

                var prompt = fixEvidence is null
                    ? $"Deliver stage {stage.Id}: {stage.Goal}."
                    : $"Previous session claimed success but the gates disagree:\n{fixEvidence}\nFix stage {stage.Id} properly.";
                var kind = fixEvidence is null ? "session" : "fix session";

                Console.WriteLine($"  [{stage.Id}] {kind} #{state.SessionsSpawned} (attempt {attempt}) — {stage.Goal}");

                using var session = AgentSession.Start(
                    behaviour: Behaviour(stage.Id, spawn),
                    artifact: Path.Combine(WorkDir, stage.Artifact),
                    mustContain: stage.MustContain,
                    rawLogPath: Path.Combine(WorkDir, $"session-{state.SessionsSpawned:000}.jsonl"),
                    prompt);

                var outcome = Watchdog.Supervise(session, SessionLimits,
                    ev => Console.WriteLine($"      [{ev.Kind,-6}] {ev.Text}"));
                Console.WriteLine($"        outcome: {outcome.Kind} — {outcome.Detail}");

                if (outcome.Kind is "Stalled" or "TimedOut")
                {
                    // The agent's fault: kill happened in the watchdog, an attempt is burned.
                    state.StallsCaught++;
                    state.Save(statePath);
                    attempt++;
                    fixEvidence = null;
                    continue;
                }

                if (outcome.Kind is "LimitBackoff")
                {
                    // The world's fault: wait, retry the same attempt. No attempt burned.
                    Console.WriteLine("        backing off, retrying the SAME attempt — not the agent's fault");
                    state.Save(statePath);
                    Thread.Sleep(500);
                    continue;
                }

                if (outcome.Kind is "AgentError")
                {
                    state.Save(statePath);
                    attempt++;
                    fixEvidence = null;
                    continue;
                }

                // outcome == Completed. The agent says it's done. So: what does the WORLD say?
                var gates = Gates.Run(WorkDir, stage);
                foreach (var g in gates)
                    Console.WriteLine($"        gate {g.Name,-16} {(g.Passed ? "PASS" : "FAIL")}  {g.Evidence}");

                if (gates.All(g => g.Passed))
                {
                    state.CompletedStages.Add(stage.Id);
                    state.Save(statePath); // checkpoint = verified work, atomically recorded
                    Console.WriteLine($"        checkpoint: {stage.Id} delivered and verified\n");
                    break;
                }

                // The agent reported success and the gates disagree. The next session gets
                // the actual failing evidence in its prompt — not a vague "try again".
                state.LiesCaught++;
                state.Save(statePath);
                fixEvidence = string.Join("\n", gates.Where(g => !g.Passed)
                    .Select(g => $"  gate {g.Name}: {g.Evidence}"));
                attempt++;
            }

            if (!state.CompletedStages.Contains(stage.Id))
            {
                Console.WriteLine($"  [{stage.Id}] out of attempts — stopping the run here");
                return;
            }

            if (i + 1 == crashAfterStage)
            {
                Console.WriteLine("  == power cut ==");
                Environment.Exit(137); // no finally blocks, no graceful shutdown — gone
            }
        }

        Console.WriteLine($"  delivered {state.CompletedStages.Count}/{Plan.Length} stages" +
                          $" · sessions: {state.SessionsSpawned}" +
                          $" · lies caught by gates: {state.LiesCaught}" +
                          $" · stalls caught by watchdog: {state.StallsCaught}");
        Console.WriteLine($"  resumed after crash: {state.ResumedAfterCrash}");
    }
}
