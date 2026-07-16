// 06 — Crash-safe orchestration with one JSON file.
//
// Distilled from Conductor's RunState:
// https://github.com/shaahink/conductor/blob/master/src/Conductor/Models/RunState.cs
//
// A long-running orchestrator must survive Ctrl+C, reboots and power cuts at ANY point.
// No database — three habits:
//   1. persist the whole state on EVERY transition,
//   2. write atomically (tmp file + rename) so a torn write is impossible,
//   3. model interrupted/owed work as explicit Pending* records, so restart = "look at the
//      state, do whatever is owed" rather than "reconstruct what was happening".
//
// The demo runs a worker (spawned from this same exe) that is killed at step 4 of 8,
// then reruns it and shows the second run resuming from step 5.

using System.Text.Json;

var dir = Path.Combine(AppContext.BaseDirectory, "state-demo");
var statePath = Path.Combine(dir, "state.json");

if (args is ["--work", ..])
{
    var crashAt = args.Length > 1 ? int.Parse(args[1]) : -1;
    Worker.Run(statePath, crashAt);
    return;
}

if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
Directory.CreateDirectory(dir);

Console.WriteLine("run 1: crashes at step 4 —");
RunWorker(crashAt: 4);
Console.WriteLine($"\nstate on disk after the crash:\n  {File.ReadAllText(statePath)}\n");

Console.WriteLine("run 2: same command, no crash —");
RunWorker(crashAt: -1);
Console.WriteLine($"\nfinal state:\n  {File.ReadAllText(statePath)}");

void RunWorker(int crashAt)
{
    var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
    };
    psi.ArgumentList.Add("--work");
    psi.ArgumentList.Add(crashAt.ToString());
    using var proc = System.Diagnostics.Process.Start(psi)!;
    Console.Write(proc.StandardOutput.ReadToEnd());
    proc.WaitForExit();
}

// ───────────────────────────── the state ─────────────────────────────

public sealed class RunState
{
    public int LastCompletedStep { get; set; }
    public List<string> History { get; set; } = [];
    /// <summary>Owed work survives the crash as data — restart re-does obligations, not vibes.</summary>
    public string? PendingFix { get; set; }
    public DateTime? UpdatedUtc { get; set; }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    public static RunState LoadOrNew(string path)
    {
        if (!File.Exists(path)) return new RunState();
        try
        {
            return JsonSerializer.Deserialize<RunState>(File.ReadAllText(path), Opts) ?? new RunState();
        }
        catch (JsonException)
        {
            // A corrupt file must never brick the orchestrator: keep the evidence, start clean.
            File.Move(path, path + ".corrupt", overwrite: true);
            return new RunState();
        }
    }

    public void Save(string path)
    {
        UpdatedUtc = DateTime.UtcNow;
        // Atomic on NTFS/ext4: the file at `path` is always either the old state or the new
        // state, never a torn half-write — even if the process dies mid-WriteAllText.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
        File.Move(tmp, path, overwrite: true);
    }
}

// ───────────────────────────── the worker ─────────────────────────────

public static class Worker
{
    public static void Run(string statePath, int crashAt)
    {
        var state = RunState.LoadOrNew(statePath);

        if (state.PendingFix is { } owed)
        {
            Console.WriteLine($"  [worker] resuming — owed work found: \"{owed}\"");
            state.PendingFix = null;
            state.Save(statePath);
        }

        for (var step = state.LastCompletedStep + 1; step <= 8; step++)
        {
            Console.WriteLine($"  [worker] step {step}/8");
            Thread.Sleep(100);

            // Transition first, persist immediately — the crash window between "did the work"
            // and "recorded the work" is as small as the filesystem allows.
            state.LastCompletedStep = step;
            state.History.Add($"step {step} done");
            if (step == 3) state.PendingFix = "re-run the flaky gate from step 3";
            state.Save(statePath);

            if (step == crashAt)
            {
                Console.WriteLine("  [worker] == simulated power cut ==");
                Environment.Exit(137); // no finally blocks, no graceful shutdown — gone
            }
        }

        Console.WriteLine("  [worker] all 8 steps complete");
    }
}
