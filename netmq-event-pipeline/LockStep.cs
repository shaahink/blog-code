// DEALER/ROUTER — lock-step: buying determinism over sockets.
//
// Distilled from the venue bridge of a production trading engine. The problem: a data feed
// (bars) and a command channel (orders) are asynchronous by default, so bar N+1 can overtake
// the orders bar N produced, and every run interleaves differently. The fix is a protocol,
// not a transport feature: the venue publishes a bar and then BLOCKS until the engine replies
// `bar_done` echoing that bar's sequence number, together with every command the bar produced.
// One total order of data and commands — a run over the same bars replays identically.
//
// DEALER/ROUTER rather than REQ/REP because the engine may say nothing, or say several
// things, per bar — REQ/REP's rigid request-reply-request lockstep is too strict a corset.
// ROUTER frames are [identity][payload]: replies are addressed, so several venues could
// share one engine.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetMQ;
using NetMQ.Sockets;

public static class LockStep
{
    public static void Run(string endpoint, int bars)
    {
        var engine = new Thread(() => Engine(endpoint)) { IsBackground = true, Name = "engine" };
        engine.Start();
        Thread.Sleep(300); // let the ROUTER bind before the venue's DEALER connects

        Venue(endpoint, bars);
        engine.Join(TimeSpan.FromSeconds(5));
    }

    // ── the venue (broker-side stand-in) ──────────────────────────────────────────────

    private static void Venue(string endpoint, int bars)
    {
        using var dealer = new DealerSocket();
        dealer.Options.Identity = Encoding.UTF8.GetBytes("venue-1");
        dealer.Connect(endpoint);

        dealer.SendFrame(Wire("hello", new { v = 1, symbol = "EURUSD" }));

        var rng = new Random(7);
        var price = 1.1000m;

        for (var seq = 1; seq <= bars; seq++)
        {
            price = decimal.Round(price + (decimal)(rng.NextDouble() - 0.45) * 0.004m, 5);
            dealer.SendFrame(Wire("bar", new { v = 1, seq, symbol = "EURUSD", close = price }));

            // LOCK-STEP: block until the engine acknowledges THIS bar. The ack carries the
            // commands the bar produced; execute them and echo executions back before moving on.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var acked = false;
            while (!acked && DateTime.UtcNow < deadline)
            {
                if (!dealer.TryReceiveFrameString(TimeSpan.FromMilliseconds(200), out var reply))
                    continue;

                using var doc = JsonDocument.Parse(reply);
                if (doc.RootElement.GetProperty("type").GetString() != "bar_done") continue;
                if (doc.RootElement.GetProperty("seq").GetInt32() != seq) continue; // stale ack

                foreach (var cmd in doc.RootElement.GetProperty("commands").EnumerateArray())
                {
                    var kind = cmd.GetProperty("kind").GetString()!;
                    Console.WriteLine($"[venue ] bar {seq,3} -> executing {kind} @ {price:F5}");
                    dealer.SendFrame(Wire("exec", new { v = 1, seq, kind, price }));
                }
                acked = true;
            }

            if (!acked)
            {
                Console.WriteLine($"[venue ] bar {seq}: engine never acked — aborting run");
                break;
            }
        }

        dealer.SendFrame(Wire("goodbye", new { v = 1 }));
    }

    // ── the engine ────────────────────────────────────────────────────────────────────

    private static void Engine(string endpoint)
    {
        using var router = new RouterSocket();
        router.Bind(endpoint);

        decimal? prevClose = null;
        var inPosition = false;
        var entryPrice = 0m;
        int barsSeen = 0, entries = 0, execs = 0;

        while (true)
        {
            var identity = router.ReceiveFrameBytes();
            var json = router.ReceiveFrameString();

            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            if (type == "goodbye") break;
            if (type == "hello") { Console.WriteLine("[engine] venue connected"); continue; }
            if (type == "exec") { execs++; continue; }
            if (type != "bar") continue;

            var seq = doc.RootElement.GetProperty("seq").GetInt32();
            var close = doc.RootElement.GetProperty("close").GetDecimal();
            barsSeen++;

            // A toy momentum strategy — the point is the protocol, not the alpha.
            var commands = new List<object>();
            if (!inPosition && prevClose is { } p && close > p * 1.0008m)
            {
                commands.Add(new { kind = "open", qty = 1 });
                inPosition = true;
                entryPrice = close;
                entries++;
            }
            else if (inPosition && close < entryPrice * 0.9995m)
            {
                commands.Add(new { kind = "close" });
                inPosition = false;
            }
            prevClose = close;

            // The ack that closes the loop: same seq, plus every command this bar produced.
            router.SendMoreFrame(identity).SendFrame(Wire("bar_done", new { seq, commands }));
        }

        Console.WriteLine($"[engine] {barsSeen} bars, {entries} entries, {execs} executions — done");
    }

    // ── wire format ───────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Opts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string Wire(string type, object body)
    {
        var node = JsonSerializer.SerializeToNode(body, Opts)!.AsObject();
        node.Insert(0, "type", type);
        return node.ToJsonString(Opts);
    }
}
