// gated-agent-delivery — an autonomous delivery loop that never believes its agents.
//
// Companion to: https://shaahink.github.io/site/blog/gated-delivery-for-a-team-of-agents/
// Distilled from Conductor: https://github.com/shaahink/conductor
//
// The loop delivers a three-stage plan by spawning one agent session per attempt and then
// VERIFYING the work independently — the agent's own success report is never evidence.
// Along the way, everything that goes wrong in real unattended runs goes wrong here, on
// purpose and deterministically:
//
//   stage 1: the backend refuses with a usage limit  -> back off, retry, burn NO attempt
//   stage 2: the agent LIES about finishing          -> the gate battery catches it,
//                                                       a fix session gets the evidence
//   stage 3: the agent goes silent mid-task          -> the watchdog kills and retries
//   ...and a power cut between runs                  -> atomic state snapshots resume it
//
// The "agents" are this same executable spawned with --fake-agent, emitting the same
// stream-JSON shape as `claude -p --output-format stream-json`, so the demo runs with
// nothing installed.

if (args is ["--fake-agent", var behaviour, var artifact, var mustContain])
{
    FakeAgent.Run(behaviour, artifact, mustContain);
    return;
}

if (args is ["--deliver", var crashArg])
{
    Orchestrator.Deliver(crashAfterStage: int.Parse(crashArg));
    return;
}

// ── the demo driver: one delivery, interrupted by a power cut, finished by a resume ──

var workDir = Orchestrator.WorkDir;
if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);

Console.WriteLine("run 1: deliver 3 stages — the power dies after stage 2\n");
RunSelf(crashAfterStage: 2);

Console.WriteLine($"\nstate on disk after the crash:\n  {File.ReadAllText(Path.Combine(workDir, "state.json"))}\n");

Console.WriteLine("run 2: same command, nothing else —\n");
RunSelf(crashAfterStage: -1);

static void RunSelf(int crashAfterStage)
{
    var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
    };
    psi.ArgumentList.Add("--deliver");
    psi.ArgumentList.Add(crashAfterStage.ToString());
    using var proc = System.Diagnostics.Process.Start(psi)!;
    Console.Write(proc.StandardOutput.ReadToEnd());
    proc.WaitForExit();
}
