// 04 — Your coding agent has a CLI. Drive it like a process.
//
// Distilled from Conductor's AgentSession:
// https://github.com/shaahink/conductor/blob/master/src/Conductor/Core/AgentSession.cs
//
// Headless agents (claude -p --output-format stream-json, opencode run --format json) emit
// newline-delimited JSON on stdout. The driver: start the process with redirected pipes,
// template the arguments, parse the stream into typed events, tee the raw stream to a log,
// and track last-activity for stall detection. No PTY, no terminal scraping.
//
// This demo spawns ITSELF with --fake-agent so it runs without any agent installed.
// Swap AgentConfig for the real thing:
//   new AgentConfig("claude", ["-p", "{prompt}", "--output-format", "stream-json", "--verbose"], "stream-json")

using System.Text.Json;

if (args is ["--fake-agent", ..])
{
    FakeAgent.Run(prompt: args.Length > 1 ? args[1] : "(none)");
    return;
}

var cfg = new AgentConfig(
    Command: Environment.ProcessPath!,
    Args: ["--fake-agent", "{prompt}"],
    Output: "stream-json");

var rawLog = Path.Combine(Path.GetTempPath(), "cli-agent-driver-raw.jsonl");
Console.WriteLine($"raw stream teed to {rawLog}\n");

using var session = AgentSession.Start(cfg, cwd: Environment.CurrentDirectory,
    prompt: "add a /health endpoint and a smoke test", rawLogPath: rawLog);

while (!session.HasExited)
{
    while (session.TryDequeue(out var ev))
        Console.WriteLine($"  [{ev.Kind,-6}] {ev.Text}");
    Thread.Sleep(100);
}
while (session.TryDequeue(out var last))
    Console.WriteLine($"  [{last.Kind,-6}] {last.Text}");

Console.WriteLine();
Console.WriteLine($"exit code : {session.ExitCode}");
Console.WriteLine($"result    : {session.ResultText}");
Console.WriteLine($"cost      : ${session.CostUsd:0.0000}   turns: {session.NumTurns}");

// ───────────────────────────── the driver ─────────────────────────────

public sealed record AgentConfig(string Command, IReadOnlyList<string> Args, string Output);

public sealed record AgentEvent(string Kind, string Text); // system | text | tool | result | stderr | raw

public sealed class AgentSession : IDisposable
{
    private readonly System.Diagnostics.Process _proc;
    private readonly StreamWriter _raw;
    private readonly System.Collections.Concurrent.ConcurrentQueue<AgentEvent> _events = new();
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    public string? ResultText { get; private set; }
    public bool ResultIsError { get; private set; }
    public decimal? CostUsd { get; private set; }
    public int? NumTurns { get; private set; }
    public bool HasExited => _proc.HasExited;
    public int ExitCode => _proc.ExitCode;

    // The one vital sign a watchdog needs (see sample 05): when did the agent last say anything?
    public DateTime LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    private AgentSession(System.Diagnostics.Process proc, StreamWriter raw) => (_proc, _raw) = (proc, raw);

    public static AgentSession Start(AgentConfig cfg, string cwd, string prompt, string rawLogPath)
    {
        // ArgumentList, not a concatenated string: prompts contain quotes, newlines, JSON —
        // shell-quoting them is a bug factory. Templates keep the config declarative.
        var psi = new System.Diagnostics.ProcessStartInfo(cfg.Command)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in cfg.Args)
            psi.ArgumentList.Add(a.Replace("{prompt}", prompt));

        var raw = new StreamWriter(rawLogPath, append: false) { AutoFlush = true };
        var proc = new System.Diagnostics.Process { StartInfo = psi };
        var session = new AgentSession(proc, raw);

        proc.OutputDataReceived += (_, e) => session.OnLine(e.Data, stderr: false);
        proc.ErrorDataReceived += (_, e) => session.OnLine(e.Data, stderr: true);
        proc.Start();
        try { proc.StandardInput.Close(); } catch { /* agent may not read stdin */ }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return session;
    }

    public bool TryDequeue(out AgentEvent ev) => _events.TryDequeue(out ev!);

    private void OnLine(string? line, bool stderr)
    {
        if (line is null) return;
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        try { _raw.WriteLine((stderr ? "[stderr] " : "") + line); } catch { }

        if (stderr) { Push("stderr", line); return; }
        var t = line.TrimStart();
        if (!t.StartsWith('{')) { Push("raw", line); return; }

        try
        {
            using var doc = JsonDocument.Parse(t);
            ParseClaudeStreamJson(doc.RootElement);
        }
        catch (JsonException) { Push("raw", line); }
    }

    // The claude `-p --output-format stream-json` shape: system / assistant / result envelopes.
    private void ParseClaudeStreamJson(JsonElement root)
    {
        switch (root.TryGetProperty("type", out var ty) ? ty.GetString() : null)
        {
            case "system":
                Push("system", root.TryGetProperty("subtype", out var st) ? st.GetString() ?? "system" : "system");
                break;

            case "assistant":
                if (root.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        switch (block.TryGetProperty("type", out var bt) ? bt.GetString() : null)
                        {
                            case "text" when block.TryGetProperty("text", out var txt):
                                Push("text", (txt.GetString() ?? "").Trim());
                                break;
                            case "tool_use":
                                var name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "tool" : "tool";
                                var input = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "";
                                Push("tool", $"{name} {input}");
                                break;
                        }
                    }
                }
                break;

            case "result":
                if (root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True) ResultIsError = true;
                if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String) ResultText = res.GetString();
                if (root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number) CostUsd = c.GetDecimal();
                if (root.TryGetProperty("num_turns", out var nt) && nt.ValueKind == JsonValueKind.Number) NumTurns = nt.GetInt32();
                Push("result", ResultIsError ? "ERROR result" : "result received");
                break;

            default:
                Push("raw", root.GetRawText());
                break;
        }
    }

    private void Push(string kind, string text)
    {
        if (text.Length > 0) _events.Enqueue(new AgentEvent(kind, text.Length > 200 ? text[..200] + "…" : text));
    }

    public void Dispose()
    {
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        _proc.Dispose();
        _raw.Dispose();
    }
}

// ───────────────────────────── a fake agent to drive ─────────────────────────────

public static class FakeAgent
{
    public static void Run(string prompt)
    {
        Emit("""{"type":"system","subtype":"init"}""");
        Emit(JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new { content = new object[] { new { type = "text", text = $"Working on: {prompt}" } } },
        }));
        Emit("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file":"Program.cs"}}]}}""");
        Emit("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit","input":{"file":"Program.cs"}}]}}""");
        Emit("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"cmd":"dotnet test"}}]}}""");
        Emit("""{"type":"assistant","message":{"content":[{"type":"text","text":"Endpoint added, tests green."}]}}""");
        Emit("""{"type":"result","is_error":false,"result":"Added /health endpoint with smoke test.","total_cost_usd":0.0421,"num_turns":5}""");
    }

    private static void Emit(string line)
    {
        Console.WriteLine(line);
        Thread.Sleep(250);
    }
}
