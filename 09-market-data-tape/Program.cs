// 09 — Record the world once: market-data tapes.
//
// Distilled from Shamshir's tape layer:
//   https://github.com/shaahink/Shamshir/blob/main/src/TradingEngine.Domain/Kernel/IEventTape.cs
//   recorder mode: https://github.com/shaahink/Shamshir/blob/main/src/TradingEngine.Adapters.CTrader/TradingEngineCBot.Recorder.cs
//
// The tape is a decoupling: the DATA (recorded events, NDJSON shards on disk) is independent
// of the CONFIG (strategy, risk profile). Record the world once; every re-run against a
// different config is a new backtest with zero new data plumbing. The recorder dedups at the
// point of capture, the reader refuses out-of-order shards, and replaying the same tape twice
// produces byte-identical journals.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var dir = Path.Combine(AppContext.BaseDirectory, "tape-demo");
if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

// ── record: the venue fires BarClosed twice sometimes; the shard stays clean ──
var writer = new ShardWriter(dir);
var rng = new Random(11);
var price = 1.2000m;
var start = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
var written = 0; var duplicates = 0;

for (var i = 0; i < 300; i++)
{
    price = decimal.Round(price + (decimal)(rng.NextDouble() - 0.47) * 0.003m, 5);
    var bar = new Bar("EURUSD", "H1", start.AddHours(i), price);
    if (writer.Append(bar)) written++;
    if (i % 7 == 0 && !writer.Append(bar)) duplicates++; // venue quirk: replayed event
}
Console.WriteLine($"recorded {written} bars ({duplicates} duplicate events dropped at capture)");

// ── replay: same tape + same config twice -> byte-identical journals ──
var tape = Tape.Read(dir, "EURUSD", "H1").ToList();

var a = Journal(Replay(tape, breakoutWindow: 20));
var b = Journal(Replay(tape, breakoutWindow: 20));
Console.WriteLine($"replay 1 sha256 : {a}");
Console.WriteLine($"replay 2 sha256 : {b}");
Console.WriteLine($"byte-identical  : {a == b}");

// ── same tape, different config -> a different backtest, no new data plumbing ──
var c = Journal(Replay(tape, breakoutWindow: 50));
Console.WriteLine($"window=50 run   : {c}   (different config, same tape: {(c != a ? "different journal" : "same journal")})");

static List<string> Replay(IReadOnlyList<Bar> tape, int breakoutWindow)
{
    var closes = new List<decimal>();
    var journal = new List<string>();
    var position = 0;
    foreach (var bar in tape)
    {
        var signal = "";
        if (closes.Count >= breakoutWindow)
        {
            var window = closes.TakeLast(breakoutWindow).ToList();
            if (position == 0 && bar.Close > window.Max()) { position = 1; signal = "open"; }
            else if (position == 1 && bar.Close < window.Min()) { position = 0; signal = "close"; }
        }
        closes.Add(bar.Close);
        journal.Add($"{bar.OpenTimeUtc:o}|{bar.Close}|{position}|{signal}");
    }
    return journal;
}

static string Journal(List<string> lines) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))))[..16];

// ───────────────────────────── bar + shards ─────────────────────────────

public sealed record Bar(string Symbol, string Tf, DateTime OpenTimeUtc, decimal Close);

/// <summary>One NDJSON file per (symbol, timeframe); duplicate bar events dropped at capture.
/// Mirrors the cBot's recorder mode, which reuses the venue's own replay to page history.</summary>
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
