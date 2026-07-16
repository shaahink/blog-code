// deterministic-kernel — one queue, one pure reducer, one journal, one effect executor.
//
// Companion to: https://shaahink.github.io/site/blog/designing-a-deterministic-kernel/
// Distilled from the kernel of a production trading engine.
//
// The whole engine is a single pump:
//
//   tape event ─► queue ─► Kernel.Decide (PURE) ─► (state', effects[]) ─► journal
//                                                        │
//                                    the ONLY I/O ◄──────┘
//                                    venue feedback re-enters the same queue
//
// Three rules make it deterministic:
//   1. drain-before-advance — a bar's consequences (fills) are processed before the next bar
//   2. the reducer is pure — no I/O, no clock, no randomness inside Decide
//   3. one lossless journal record per event — same tape + same config => byte-identical journal
//
// This demo records a tape to disk once, replays it twice (identical journals, proven by
// hash), then replays the same tape under a different config — a new backtest with zero new
// data plumbing.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var dir = Path.Combine(AppContext.BaseDirectory, "tape-demo");
if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

// ── record the world once ──────────────────────────────────────────────────────────────
// The venue occasionally replays an event it already sent; the shard stays clean because
// the recorder dedups at the point of capture.
var writer = new ShardWriter(dir);
var rng = new Random(11);
var price = 1.2000m;
var start = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
int written = 0, duplicates = 0;

for (var i = 0; i < 300; i++)
{
    price = decimal.Round(price + (decimal)(rng.NextDouble() - 0.47) * 0.003m, 5);
    var bar = new Bar("EURUSD", "H1", start.AddHours(i), price);
    if (writer.Append(bar)) written++;
    if (i % 7 == 0 && !writer.Append(bar)) duplicates++; // venue quirk: replayed event
}
Console.WriteLine($"recorded {written} bars ({duplicates} duplicate events dropped at capture)");

// ── replay: same tape + same config, twice ─────────────────────────────────────────────
var tape = Tape.Read(dir, "EURUSD", "H1").ToList();
var config = new KernelConfig(Window: 20, DrawdownLimit: 0.03m);

var first = Driver.Run(tape, config);
var second = Driver.Run(tape, config);

Console.WriteLine($"journal entries : {first.Count}");
Console.WriteLine($"replay 1 sha256 : {Hash(first)}");
Console.WriteLine($"replay 2 sha256 : {Hash(second)}");
Console.WriteLine($"byte-identical  : {Hash(first) == Hash(second)}");

// ── same tape, different config: a new backtest, no new data plumbing ──────────────────
var third = Driver.Run(tape, config with { Window = 50 });
Console.WriteLine($"window=50 run   : {Hash(third)}   ({(Hash(third) != Hash(first) ? "different journal, as expected" : "same journal?!")})");

Console.WriteLine();
Console.WriteLine("last three journal records:");
foreach (var line in first.TakeLast(3))
    Console.WriteLine($"  {line}");

static string Hash(IReadOnlyList<string> journal) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', journal))))[..16];

// ───────────────────────────── events, effects, state ─────────────────────────────

public abstract record EngineEvent(DateTime AtUtc);
public sealed record BarClosed(DateTime AtUtc, decimal Close) : EngineEvent(AtUtc);
public sealed record OrderFilled(DateTime AtUtc, int Qty, decimal Price) : EngineEvent(AtUtc);

public abstract record Effect;
public sealed record SubmitOrder(int Qty, decimal RefPrice) : Effect;
public sealed record CloseAll(int Qty, decimal RefPrice, string Reason) : Effect;

/// <summary>Constant for the whole run — the knobs of a backtest. Config is NOT state:
/// keeping it out of EngineState is what makes "same tape, different config" one call.</summary>
public sealed record KernelConfig(int Window, decimal DrawdownLimit);

public sealed record EngineState(
    IReadOnlyList<decimal> Closes,
    int Position,
    decimal Cash,
    decimal Equity,
    decimal Peak,
    bool Locked)
{
    public static EngineState Initial(decimal cash) => new([], 0, cash, cash, cash, false);
}

public sealed record Decision(EngineState State, IReadOnlyList<Effect> Effects);

public sealed record StepRecord(
    long Seq, DateTime SimTimeUtc, string EventKind, string[] EffectKinds, int Position, decimal Equity);

// ───────────────────────────── the pure reducer ─────────────────────────────

public static class Kernel
{
    // No I/O, no wall clock, no Guid.NewGuid, no randomness. If the kernel needs the time,
    // the time is on the event. That is the entire determinism contract.
    public static Decision Decide(KernelConfig cfg, EngineState s, EngineEvent evt) => evt switch
    {
        BarClosed bar => OnBar(cfg, s, bar),
        OrderFilled fill => new Decision(Apply(s, fill), []),
        _ => new Decision(s, []),
    };

    private static Decision OnBar(KernelConfig cfg, EngineState s, BarClosed bar)
    {
        var closes = s.Closes.Count >= cfg.Window
            ? s.Closes.Skip(1).Append(bar.Close).ToArray()
            : s.Closes.Append(bar.Close).ToArray();

        var equity = s.Cash + s.Position * bar.Close;
        var next = s with { Closes = closes, Equity = equity, Peak = Math.Max(s.Peak, equity) };

        // Risk first, and with the power of veto: a drawdown breach flattens and locks,
        // no matter what any strategy would like to do next.
        if (next.Position > 0 && next.Equity < next.Peak * (1 - cfg.DrawdownLimit))
            return new Decision(next with { Locked = true },
                [new CloseAll(next.Position, bar.Close, "daily drawdown breached")]);

        // A toy breakout strategy: close above the prior window's high while flat.
        if (next.Position == 0 && !next.Locked && s.Closes.Count >= cfg.Window && bar.Close > s.Closes.Max())
            return new Decision(next, [new SubmitOrder(100, bar.Close)]);

        return new Decision(next, []);
    }

    private static EngineState Apply(EngineState s, OrderFilled fill) => s with
    {
        Position = s.Position + fill.Qty,
        Cash = s.Cash - fill.Qty * fill.Price,
    };
}

// ───────────────────────────── the funnel ─────────────────────────────

public static class Driver
{
    public static List<string> Run(IReadOnlyList<Bar> tape, KernelConfig cfg)
    {
        var state = EngineState.Initial(cash: 10_000m);
        var queue = new Queue<EngineEvent>();
        var journal = new List<string>();
        long seq = 0;

        foreach (var bar in tape)
        {
            queue.Enqueue(new BarClosed(bar.OpenTimeUtc, bar.Close));

            // Drain this tape event AND every feedback event it triggers before pulling the
            // next bar — the in-order property that makes replay deterministic.
            while (queue.TryDequeue(out var evt))
            {
                var decision = Kernel.Decide(cfg, state, evt);
                state = decision.State;

                // One journal record per processed event: lossless, sim-time anchored.
                journal.Add(JsonSerializer.Serialize(new StepRecord(
                    ++seq, evt.AtUtc, evt.GetType().Name,
                    [.. decision.Effects.Select(e => e.GetType().Name)],
                    state.Position, state.Equity)));

                // The ONLY place effects touch the world. Feedback re-enters the same queue.
                foreach (var effect in decision.Effects)
                    Venue.Execute(effect, queue);
            }
        }

        return journal;
    }
}

// A stand-in for the execution adapter: fills instantly at the reference price and feeds
// the fill back in as an event. A real venue is asynchronous — the queue shape is the same.
public static class Venue
{
    public static void Execute(Effect effect, Queue<EngineEvent> feedback)
    {
        switch (effect)
        {
            case SubmitOrder o:
                feedback.Enqueue(new OrderFilled(default, o.Qty, o.RefPrice));
                break;
            case CloseAll c:
                feedback.Enqueue(new OrderFilled(default, -c.Qty, c.RefPrice));
                break;
        }
    }
}

// ───────────────────────────── the tape on disk ─────────────────────────────

public sealed record Bar(string Symbol, string Tf, DateTime OpenTimeUtc, decimal Close);

/// <summary>One NDJSON file per (symbol, timeframe); duplicate bar events dropped at capture.
/// The DATA (recorded events) is independent of the CONFIG (strategy, risk) — record the
/// world once, and every re-run against a different config is free.</summary>
public sealed class ShardWriter(string dir)
{
    private readonly HashSet<(string, string, DateTime)> _seen = [];

    public bool Append(Bar bar)
    {
        if (!_seen.Add((bar.Symbol, bar.Tf, bar.OpenTimeUtc))) return false;

        Directory.CreateDirectory(dir);
        File.AppendAllText(Path.Combine(dir, $"{bar.Symbol}-{bar.Tf}.ndjson"),
            JsonSerializer.Serialize(bar) + Environment.NewLine);
        return true;
    }
}

public static class Tape
{
    /// <summary>Reads a shard back in deterministic order. Order is a CONTRACT: a tape that
    /// isn't strictly ascending is corrupt, and refusing it beats silently reordering.</summary>
    public static IEnumerable<Bar> Read(string dir, string symbol, string tf)
    {
        var path = Path.Combine(dir, $"{symbol}-{tf}.ndjson");
        DateTime? prev = null;
        foreach (var line in File.ReadLines(path))
        {
            var bar = JsonSerializer.Deserialize<Bar>(line)!;
            if (prev is { } p && bar.OpenTimeUtc <= p)
                throw new InvalidDataException($"tape corrupt: {bar.OpenTimeUtc:o} after {p:o}");
            prev = bar.OpenTimeUtc;
            yield return bar;
        }
    }
}
