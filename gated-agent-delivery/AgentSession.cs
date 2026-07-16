// A supervised agent session: a child process with structured output.
//
// Same shape as driving `claude -p "<prompt>" --output-format stream-json` — start the
// process with redirected pipes and ArgumentList (never a concatenated argument string:
// prompts contain quotes, newlines, JSON), tee the raw stream to a log before parsing,
// fold stdout lines into a small typed vocabulary, and stamp LastActivityUtc on every
// line — the one vital sign the watchdog lives on.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

public sealed record AgentEvent(string Kind, string Text); // system | text | tool | result | stderr | raw

public sealed class AgentSession : IDisposable
{
    private readonly Process _proc;
    private readonly StreamWriter _raw;
    private readonly ConcurrentQueue<AgentEvent> _events = new();
    private readonly ConcurrentQueue<string> _tail = new();
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    public string? ResultText { get; private set; }
    public bool ResultIsError { get; private set; }
    public decimal? CostUsd { get; private set; }
    public bool HasExited => _proc.HasExited;
    public int ExitCode => _proc.ExitCode;

    public DateTime LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    /// <summary>The last few raw lines — where a backend's refusal hides in free text.</summary>
    public string Tail => string.Join(" | ", _tail);

    private AgentSession(Process proc, StreamWriter raw) => (_proc, _raw) = (proc, raw);

    public static AgentSession Start(string behaviour, string artifact, string mustContain,
        string rawLogPath, string prompt)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        // In Conductor these come from a per-agent template: ["-p", "{prompt}", ...].
        // The prompt is passed for realism; the fake agent's behaviour is what it acts on.
        psi.ArgumentList.Add("--fake-agent");
        psi.ArgumentList.Add(behaviour);
        psi.ArgumentList.Add(artifact);
        psi.ArgumentList.Add(mustContain);

        var raw = new StreamWriter(rawLogPath, append: false) { AutoFlush = true };
        raw.WriteLine($"# prompt: {prompt.ReplaceLineEndings(" / ")}");

        var proc = new Process { StartInfo = psi };
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

    public void Kill()
    {
        // In Conductor a Windows Job Object with KILL_ON_JOB_CLOSE also reaps detached
        // grandchildren; entireProcessTree covers everything this demo can spawn.
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    public void WaitForExit() => _proc.WaitForExit();

    private void OnLine(string? line, bool stderr)
    {
        if (line is null) return;
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        try { _raw.WriteLine((stderr ? "[stderr] " : "") + line); } catch { }

        _tail.Enqueue(line);
        while (_tail.Count > 8) _tail.TryDequeue(out _);

        if (stderr) { Push("stderr", line); return; }
        var t = line.TrimStart();
        if (!t.StartsWith('{')) { Push("raw", line); return; }

        try
        {
            using var doc = JsonDocument.Parse(t);
            ParseStreamJson(doc.RootElement);
        }
        catch (JsonException) { Push("raw", line); }
    }

    // The claude-style stream-json shape: system / assistant / result envelopes. Unknown
    // event types become "raw", never a crash — the format is versioned by vibes.
    private void ParseStreamJson(JsonElement root)
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
                Push("result", ResultText ?? (ResultIsError ? "ERROR result" : "result received"));
                break;

            default:
                Push("raw", root.GetRawText());
                break;
        }
    }

    private void Push(string kind, string text)
    {
        if (text.Length > 0) _events.Enqueue(new AgentEvent(kind, text.Length > 160 ? text[..160] + "…" : text));
    }

    public void Dispose()
    {
        Kill();
        _proc.Dispose();
        _raw.Dispose();
    }
}
