// 10 — CI for a trading engine, no broker account required.
//
// Distilled from Shamshir's simulation test tier:
// https://github.com/shaahink/Shamshir/tree/main/tests/TradingEngine.Tests.Simulation
//
// The scariest code in a trading system is the venue integration — and it's exactly the code
// unit tests never touch. The simulation tier closes that gap: a FAKE venue that speaks the
// REAL wire protocol drives the REAL engine through scripted scenarios. No credentials, no
// market hours, no network — it runs on every push.
//
// Scenario here: a rally that triggers one entry, then a crash that must trip the drawdown
// governor. The harness asserts on the exact command sequence the venue observed.

using SimulationTier;

var scripted = Script.RallyThenCrash();
var venue = new FakeVenue(scripted);
var engine = new Engine(drawdownLimit: 0.02m);

// The lock-step loop from sample 02, in-process: venue feeds a bar, engine answers with
// commands, venue applies them and echoes executions. Same protocol, zero sockets.
foreach (var bar in venue.Bars())
{
    var commands = engine.OnBar(bar, venue.Equity);
    venue.Apply(bar, commands);
}

Console.WriteLine($"bars fed        : {scripted.Count}");
Console.WriteLine($"commands seen   : {string.Join(", ", venue.CommandLog)}");
Console.WriteLine();

// ── the assertions a broker sandbox can't give you deterministically ──
var checks = new (string Name, bool Pass)[]
{
    ("exactly one entry", venue.CommandLog.Count(c => c == "open") == 1),
    ("drawdown breach flattens", venue.CommandLog.Contains("close-all")),
    ("no orders after the lock", venue.CommandLog.SkipWhile(c => c != "close-all").Skip(1).All(c => c != "open")),
    ("account never breaches -2% from peak by more than one bar's gap",
        venue.WorstDrawdown < 0.05m),
};

var failed = 0;
foreach (var (name, pass) in checks)
{
    Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name}");
    if (!pass) failed++;
}
Environment.Exit(failed == 0 ? 0 : 1);

namespace SimulationTier
{
    public sealed record Bar(DateTime AtUtc, decimal Close);

    public static class Script
    {
        /// <summary>Deterministic by construction — a scripted scenario, not a random walk:
        /// flat, rally (entry), then a crash steep enough to trip a 2% drawdown governor.</summary>
        public static IReadOnlyList<Bar> RallyThenCrash()
        {
            var start = new DateTime(2024, 6, 3, 0, 0, 0, DateTimeKind.Utc);
            var closes = new List<decimal>();
            for (var i = 0; i < 25; i++) closes.Add(100m + (i % 3) * 0.1m);     // flat chop
            for (var i = 1; i <= 10; i++) closes.Add(100.3m + i * 0.4m);        // rally -> breakout
            for (var i = 1; i <= 12; i++) closes.Add(104.3m - i * 0.9m);        // crash -> governor
            return [.. closes.Select((c, i) => new Bar(start.AddHours(i), c))];
        }
    }

    /// <summary>The engine under test: toy strategy + a risk governor with the power of veto.
    /// In Shamshir this is the real kernel; the point is that THIS code is what CI exercises.</summary>
    public sealed class Engine(decimal drawdownLimit)
    {
        private readonly List<decimal> _closes = [];
        private int _position;
        private decimal _peakEquity;
        private bool _locked;

        public IReadOnlyList<string> OnBar(Bar bar, decimal equity)
        {
            _peakEquity = Math.Max(_peakEquity, equity);

            // Governor first, veto always: strategies propose, risk disposes.
            if (_position > 0 && equity < _peakEquity * (1 - drawdownLimit))
            {
                _position = 0;
                _locked = true;
                _closes.Add(bar.Close);
                return ["close-all"];
            }

            var commands = new List<string>();
            if (!_locked && _position == 0 && _closes.Count >= 20 && bar.Close > _closes.TakeLast(20).Max())
            {
                _position = 1;
                commands.Add("open");
            }
            _closes.Add(bar.Close);
            return commands;
        }
    }

    /// <summary>The fake venue: owns the account, applies commands, tracks what the engine
    /// asked for. In Shamshir, FakeCBot does this over real NetMQ sockets so the transport
    /// and handshake are exercised too.</summary>
    public sealed class FakeVenue(IReadOnlyList<Bar> script)
    {
        private decimal _cash = 10_000m;
        private int _qty;
        private decimal _lastClose;
        private decimal _peak = 10_000m;

        public List<string> CommandLog { get; } = [];
        public decimal Equity => _cash + _qty * _lastClose;
        public decimal WorstDrawdown { get; private set; }

        public IEnumerable<Bar> Bars()
        {
            foreach (var bar in script)
            {
                _lastClose = bar.Close;
                _peak = Math.Max(_peak, Equity);
                WorstDrawdown = Math.Max(WorstDrawdown, _peak == 0 ? 0 : (_peak - Equity) / _peak);
                yield return bar;
            }
        }

        public void Apply(Bar bar, IReadOnlyList<string> commands)
        {
            foreach (var cmd in commands)
            {
                CommandLog.Add(cmd);
                switch (cmd)
                {
                    case "open": _qty = 100; _cash -= 100 * bar.Close; break;
                    case "close-all": _cash += _qty * bar.Close; _qty = 0; break;
                }
            }
        }
    }
}
