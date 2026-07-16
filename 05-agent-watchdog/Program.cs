// 05 — Watchdogging an autonomous agent: stall, timeout, limit-backoff.
//
// Distilled from Conductor's supervision loop:
// https://github.com/shaahink/conductor/blob/master/src/Conductor/Core/Orchestrator.cs
//
// An unattended agent fails in ways an exit code never reports: it goes silent mid-task,
// it runs forever, or the backend refuses it with a rate limit. The watchdog reduces every
// session to an explicit outcome — and the outcomes get DIFFERENT next moves. A stall is the
// agent's fault (kill, resume, burn an attempt). A rate limit is the world's fault (wait,
// resume, burn nothing).
//
// This demo supervises three child processes (spawned from this same exe) that exhibit each
// failure. Deliberately small limits so the run takes seconds.

using System.Diagnostics;
using System.Text.RegularExpressions;

if (args is ["--child", var mode])
{
    Child.Run(mode);
    return;
}

var limits = new Limits(StallSeconds: 3, TimeoutSeconds: 20);

foreach (var childMode in (string[])["ok", "stall", "limit"])
{
    var outcome = Watchdog.Supervise(childMode, limits);
    Console.WriteLine($"child '{childMode}'  ->  {outcome.Kind}   {outcome.Detail}");
    Console.WriteLine($"   next move: {outcome.NextMove}\n");
}

public sealed record Limits(int StallSeconds, int TimeoutSeconds);

public sealed record Outcome(string Kind, string Detail, string NextMove);

// ───────────────────────────── the watchdog ─────────────────────────────

public static class Watchdog
{
    // Same shape as Conductor's LimitRx: the backend's refusal hides in free text.
    private static readonly Regex LimitRx = new("rate.?limit|usage.?limit|quota|overloaded",
        RegexOptions.IgnoreCase);

    public static Outcome Supervise(string mode, Limits limits)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--child");
        psi.ArgumentList.Add(mode);

        var lastLines = new Queue<string>();
        long lastActivityTicks = DateTime.UtcNow.Ticks;

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
            lock (lastLines)
            {
                lastLines.Enqueue(e.Data);
                if (lastLines.Count > 10) lastLines.Dequeue(); // keep a tail for diagnosis
            }
        };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        var started = DateTime.UtcNow;

        bool stalled = false, timedOut = false;
        while (!proc.HasExited)
        {
            var lastActivity = new DateTime(Interlocked.Read(ref lastActivityTicks), DateTimeKind.Utc);

            if ((DateTime.UtcNow - lastActivity).TotalSeconds > limits.StallSeconds)
            {
                stalled = true;
                Kill(proc); // in Conductor a Windows Job Object also reaps detached grandchildren
                break;
            }
            if ((DateTime.UtcNow - started).TotalSeconds > limits.TimeoutSeconds)
            {
                timedOut = true;
                Kill(proc);
                break;
            }
            Thread.Sleep(200);
        }
        proc.WaitForExit();

        string tail;
        lock (lastLines) tail = string.Join(" | ", lastLines);

        // The judgement ladder. Order matters: a killed process also has a nonzero exit code,
        // so the reasons we killed it are checked before the exit code is read as "error".
        if (stalled)
            return new Outcome("Stalled", $"no output for {limits.StallSeconds}s",
                "kill, then resume the SAME session (bounded resume budget) — an attempt is burned");
        if (timedOut)
            return new Outcome("TimedOut", $"exceeded {limits.TimeoutSeconds}s",
                "kill, then resume with a smaller ask — an attempt is burned");
        if (proc.ExitCode != 0 && LimitRx.IsMatch(tail))
            return new Outcome("LimitBackoff", "backend refused (rate/usage limit)",
                "wait N minutes, resume the SAME session — NO attempt burned, not the agent's fault");
        if (proc.ExitCode != 0)
            return new Outcome("AgentError", $"exit code {proc.ExitCode}",
                "inspect the tail, spawn a fix session with the evidence embedded");

        return new Outcome("Completed", "exit 0",
            "now VERIFY independently — gates, commits, tracker — before believing it (see Conductor's trust model)");
    }

    private static void Kill(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }
}

// ───────────────────────────── misbehaving children ─────────────────────────────

public static class Child
{
    public static void Run(string mode)
    {
        switch (mode)
        {
            case "ok":
                for (var i = 1; i <= 5; i++) { Console.WriteLine($"working… step {i}/5"); Thread.Sleep(400); }
                Console.WriteLine("done");
                break;

            case "stall":
                Console.WriteLine("working… step 1");
                Console.WriteLine("working… step 2");
                Thread.Sleep(Timeout.Infinite); // silence — the exit code will never come
                break;

            case "limit":
                Console.WriteLine("working… step 1");
                Console.WriteLine("Error: usage limit reached, try again at 03:00");
                Environment.Exit(1);
                break;
        }
    }
}
