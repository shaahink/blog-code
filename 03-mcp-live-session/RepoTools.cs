using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace McpLiveSession;

/// <summary>
/// The live-session pattern: `analyze` does the expensive work ONCE and returns a handle;
/// every other tool answers from the warm snapshot. Failures come back as short, actionable
/// envelopes — see sample 07 for why.
/// </summary>
[McpServerToolType]
public sealed class RepoTools
{
    private sealed record Snapshot(string Handle, string Root, IReadOnlyList<string> Files, DateTime AnalyzedAtUtc);

    private readonly ConcurrentDictionary<string, Snapshot> _sessions = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [McpServerTool]
    [Description("Analyse a directory into an in-memory snapshot and open a live session. Returns a handle the other tools accept.")]
    public string Analyze([Description("Absolute path of a directory")] string path)
    {
        if (!Directory.Exists(path))
            return Envelope($"Directory not found: {path}",
                "Pass an absolute path that exists.", "analyze(\"C:/repos/MyApp\")");

        // Stand-in for the expensive part (DevContext runs a full Roslyn analysis here).
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
            .Take(20_000)
            .Select(f => Path.GetRelativePath(path, f).Replace('\\', '/'))
            .ToList();

        var handle = $"s-{Guid.NewGuid():N}"[..8];
        _sessions[handle] = new Snapshot(handle, path, files, DateTime.UtcNow);
        return JsonSerializer.Serialize(new { handle, root = path, files = files.Count }, JsonOpts);
    }

    [McpServerTool]
    [Description("Find files in an analysed session by substring. Omit handle when only one session is open.")]
    public string FindFiles(
        [Description("Substring to match against relative paths")] string query,
        [Description("Session handle from analyze()")] string? handle = null)
    {
        var (snapshot, error) = Resolve(handle);
        if (snapshot is null) return error!;

        var hits = snapshot.Files
            .Where(f => f.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(50)
            .ToList();

        if (hits.Count == 0)
            return Envelope($"No file matched '{query}' in {snapshot.Root}.",
                "Try a shorter substring, or list_sessions() to check you analysed the right root.",
                "find_files(\"Controller\")");

        return JsonSerializer.Serialize(new { handle = snapshot.Handle, count = hits.Count, files = hits }, JsonOpts);
    }

    [McpServerTool]
    [Description("List the open sessions with their handles.")]
    public string ListSessions() =>
        JsonSerializer.Serialize(_sessions.Values
            .Select(s => new { s.Handle, s.Root, files = s.Files.Count, ageSeconds = (int)(DateTime.UtcNow - s.AnalyzedAtUtc).TotalSeconds }), JsonOpts);

    // Handle resolution mirrors DevContext: an explicit handle wins; with exactly one open
    // session it is implied; otherwise the error tells the agent precisely what to do next.
    private (Snapshot? Snapshot, string? Error) Resolve(string? handle)
    {
        if (handle is not null)
            return _sessions.TryGetValue(handle, out var s)
                ? (s, null)
                : (null, Envelope($"Unknown handle '{handle}'.",
                    "Handles expire with the server — analyze again or check list_sessions().",
                    "analyze(\"C:/repos/MyApp\")"));

        return _sessions.Count switch
        {
            0 => (null, Envelope("No active session.",
                "Run analyze first.", "analyze(\"C:/repos/MyApp\")")),
            1 => (_sessions.Values.First(), null),
            _ => (null, Envelope("Multiple sessions open — pass a handle.",
                "Call list_sessions() and pick one.", "find_files(\"Order\", handle: \"s-1a2b3c4d\")")),
        };
    }

    // {error, hint, example} — short enough that a failed call costs the agent almost nothing.
    private static string Envelope(string error, string hint, string example) =>
        JsonSerializer.Serialize(new { error, hint, example }, JsonOpts);
}
