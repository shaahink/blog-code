// A scripted stand-in for a real orchestrator: it "delivers" a four-stage plan on a loop,
// complete with agent chatter, a lying agent caught by the gate battery, and a fix session.
// Every state change is one RunEvent, fanned out to every SSE subscriber. New subscribers
// get a replay of recent history first, so the TUI paints a full picture immediately.

using System.Collections.Concurrent;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

public sealed record RunEvent(int Seq, string Kind, string Stage, string State, string Text);
// Kind: run | stage | agent | gate    State (run):   running | paused | completed
//                                     State (stage): pending | running | failed | delivered

public sealed class RunSimulator
{
    private static readonly (string Id, string Title)[] Stages =
    [
        ("S1", "survey the change"),
        ("S2", "implement the slice"),
        ("S3", "prove it with tests"),
        ("S4", "write the runbook"),
    ];

    private readonly ConcurrentDictionary<Guid, Channel<RunEvent>> _subs = new();
    private readonly Queue<RunEvent> _history = new();
    private readonly Lock _gate = new();
    private int _seq;
    private volatile bool _paused;
    private volatile bool _restart;

    public bool Apply(string action)
    {
        switch (action)
        {
            case "pause":
                _paused = true;
                Emit("run", "", "paused", "paused by operator");
                return true;
            case "resume":
                _paused = false;
                Emit("run", "", "running", "resumed by operator");
                return true;
            case "restart":
                _restart = true;
                _paused = false;
                Emit("run", "", "running", "restart requested by operator");
                return true;
            default:
                return false;
        }
    }

    public async IAsyncEnumerable<SseItem<RunEvent>> Subscribe([EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var ch = Channel.CreateUnbounded<RunEvent>();
        RunEvent[] replay;
        lock (_gate)
        {
            replay = [.. _history];
            _subs[id] = ch;
        }

        try
        {
            foreach (var e in replay)
                yield return new SseItem<RunEvent>(e, "run");
            await foreach (var e in ch.Reader.ReadAllAsync(ct))
                yield return new SseItem<RunEvent>(e, "run");
        }
        finally
        {
            _subs.TryRemove(id, out _);
        }
    }

    public async Task RunLoopAsync()
    {
        for (var cycle = 1; ; cycle++)
        {
            _restart = false;
            Emit("run", "", "running", $"delivery run #{cycle} started");
            foreach (var (id, title) in Stages)
                Emit("stage", id, "pending", title);

            foreach (var (id, title) in Stages)
            {
                Emit("stage", id, "running", title);
                Emit("agent", id, "", $"session started — {title}");

                foreach (var line in AgentLines(id))
                {
                    if (await Wait(350, 850)) goto nextCycle;
                    Emit("agent", id, "", line);
                }

                if (id == "S3")
                {
                    // The scripted lie: the agent reports green, the battery disagrees.
                    Emit("agent", id, "", "claims: \"all tests passing, done\"");
                    if (await Wait(500, 900)) goto nextCycle;
                    Emit("gate", id, "", "gate build   PASS  (exit 0)");
                    Emit("gate", id, "", "gate tests   FAIL  2/14 failing: OrderTests.Rounding");
                    Emit("stage", id, "failed", title);
                    if (await Wait(700, 1100)) goto nextCycle;
                    Emit("agent", id, "", "fix session: failing test output embedded in prompt");
                    Emit("agent", id, "", "rounding uses banker's rounding — fixing the assertion the right way");
                    if (await Wait(700, 1100)) goto nextCycle;
                }

                Emit("gate", id, "", "gate build   PASS  (exit 0)");
                Emit("gate", id, "", "gate tests   PASS  (14/14)");
                Emit("stage", id, "delivered", title);
                if (await Wait(400, 800)) goto nextCycle;
            }

            Emit("run", "", "completed", $"run #{cycle} delivered — next run in 4 s");
            if (await Wait(4000, 4001)) continue;

        nextCycle: ;
        }
    }

    private static string[] AgentLines(string stage) => stage switch
    {
        "S1" => ["reading the issue and the touched modules", "map: three files, one migration, no API break"],
        "S2" => ["writing the slice end-to-end", "wiring the handler into the pipeline", "self-review: naming, edges, allocations"],
        "S3" => ["running the suite", "14 tests collected"],
        "S4" => ["drafting the runbook entry", "linking the dashboard and the rollback step"],
        _ => [],
    };

    /// <summary>Sleeps a beat (honouring pause). True means a restart was requested.</summary>
    private async Task<bool> Wait(int minMs, int maxMs)
    {
        await Task.Delay(Random.Shared.Next(minMs, maxMs));
        while (_paused && !_restart)
            await Task.Delay(150);
        return _restart;
    }

    private void Emit(string kind, string stage, string state, string text)
    {
        lock (_gate)
        {
            var e = new RunEvent(++_seq, kind, stage, state, text);
            _history.Enqueue(e);
            while (_history.Count > 120) _history.Dequeue();
            foreach (var ch in _subs.Values)
                ch.Writer.TryWrite(e);
        }
    }
}
