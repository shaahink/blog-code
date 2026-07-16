// 01 — Designing a kernel: one queue, one pure reducer, one journal, one effect executor.
//
// Distilled from Shamshir's KernelDriver ("the funnel"):
// https://github.com/shaahink/Shamshir/blob/main/src/TradingEngine.Engine/Kernel/KernelDriver.cs
//
// The whole engine is a single pump: tape event -> queue -> pure Decide -> journal -> effects.
// Venue feedback (fills) re-enters the queue and is drained BEFORE the next tape event, so there
// is exactly one total order of events. Same tape in, byte-identical journal out — every time.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var tape = Tape.Synthetic(seed: 42, bars: 250);

var first = Driver.Run(tape);
var second = Driver.Run(tape);

Console.WriteLine($"journal entries : {first.Count}");
Console.WriteLine($"run 1 sha256    : {Hash(first)}");
Console.WriteLine($"run 2 sha256    : {Hash(second)}");
Console.WriteLine($"byte-identical  : {Hash(first) == Hash(second)}");
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

// ───────────────────────────── the pure kernel ─────────────────────────────

public static class Kernel
{
    private const int Window = 20;
    private const decimal DrawdownLimit = 0.03m;

    // No I/O, no wall clock, no Guid.NewGuid, no randomness. If the kernel needs the time,
    // the time is on the event. That is the entire determinism contract.
    public static Decision Decide(EngineState s, EngineEvent evt) => evt switch
    {
        BarClosed bar => OnBar(s, bar),
        OrderFilled fill => new Decision(Apply(s, fill), []),
        _ => new Decision(s, []),
    };

    private static Decision OnBar(EngineState s, BarClosed bar)
    {
        var closes = s.Closes.Count >= Window
            ? s.Closes.Skip(1).Append(bar.Close).ToArray()
            : s.Closes.Append(bar.Close).ToArray();

        var equity = s.Cash + s.Position * bar.Close;
        var next = s with { Closes = closes, Equity = equity, Peak = Math.Max(s.Peak, equity) };

        // Risk first, and with the power of veto: a drawdown breach flattens and locks,
        // no matter what any strategy would like to do next.
        if (next.Position > 0 && next.Equity < next.Peak * (1 - DrawdownLimit))
            return new Decision(next with { Locked = true },
                [new CloseAll(next.Position, bar.Close, "daily drawdown breached")]);

        // A toy breakout strategy: close above the prior window's high while flat.
        if (next.Position == 0 && !next.Locked && s.Closes.Count >= Window && bar.Close > s.Closes.Max())
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
    public static List<string> Run(IReadOnlyList<EngineEvent> tape)
    {
        var state = EngineState.Initial(cash: 10_000m);
        var queue = new Queue<EngineEvent>();
        var journal = new List<string>();
        long seq = 0;

        foreach (var tapeEvent in tape)
        {
            queue.Enqueue(tapeEvent);

            // Drain this tape event AND every feedback event it triggers before pulling the
            // next bar — the in-order property that makes replay deterministic.
            while (queue.TryDequeue(out var evt))
            {
                var decision = Kernel.Decide(state, evt);
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

// ───────────────────────────── a synthetic tape ─────────────────────────────

public static class Tape
{
    public static IReadOnlyList<EngineEvent> Synthetic(int seed, int bars)
    {
        var rng = new Random(seed);
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var price = 100m;
        var events = new List<EngineEvent>(bars);
        for (var i = 0; i < bars; i++)
        {
            price += (decimal)(rng.NextDouble() - 0.48) * 2m; // gentle upward drift
            events.Add(new BarClosed(start.AddHours(i), decimal.Round(price, 4)));
        }
        return events;
    }
}
