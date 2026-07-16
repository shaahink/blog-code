// 02 — A deterministic, event-driven bridge between two processes with ZeroMQ.
//
// Distilled from Shamshir's cTrader bridge:
//   venue side  https://github.com/shaahink/Shamshir/blob/main/src/TradingEngine.Adapters.CTrader/TradingEngineCBot.Events.cs
//   engine side https://github.com/shaahink/Shamshir/blob/main/src/TradingEngine.Infrastructure/Transport/NetMq/NetMqMessageTransport.cs
//
// The trick that makes the bridge deterministic is the LOCK-STEP loop: the venue publishes a
// bar and then BLOCKS until the engine replies `bar_done` echoing that bar's sequence number,
// together with every command the bar produced. Bar N+1 cannot overtake the orders of bar N,
// so a run over the same bars always interleaves data and commands identically.
//
// Here both roles run in one process (engine on a background thread) so the demo is a single
// `dotnet run` — the sockets are real TCP, the processes may as well be on different machines.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetMQ;
using NetMQ.Sockets;

const string CommandEndpoint = "tcp://127.0.0.1:15956";
const int Bars = 40;

var engine = new Thread(() => Engine.Run(CommandEndpoint)) { IsBackground = true, Name = "engine" };
engine.Start();
Thread.Sleep(400); // let the ROUTER bind before the venue's DEALER connects

Venue.Run(CommandEndpoint, Bars);

engine.Join(TimeSpan.FromSeconds(5));
NetMQConfig.Cleanup(block: false);

// ───────────────────────────── the venue (cBot stand-in) ─────────────────────────────

public static class Venue
{
    public static void Run(string endpoint, int bars)
    {
        using var dealer = new DealerSocket();
        dealer.Options.Identity = Encoding.UTF8.GetBytes("venue-1");
        dealer.Connect(endpoint);

        dealer.SendFrame(Wire.Json("hello", new { v = 1, symbol = "EURUSD" }));

        var rng = new Random(7);
        var price = 1.1000m;

        for (var seq = 1; seq <= bars; seq++)
        {
            price += (decimal)(rng.NextDouble() - 0.45) * 0.004m; // gentle upward drift
            price = decimal.Round(price, 5);
            dealer.SendFrame(Wire.Json("bar", new { v = 1, seq, symbol = "EURUSD", close = price }));

            // LOCK-STEP: block until the engine acknowledges THIS bar. The ack carries the
            // commands the bar produced; apply them and echo executions back before moving on.
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
                    dealer.SendFrame(Wire.Json("exec", new { v = 1, seq, kind, price }));
                }
                acked = true;
            }

            if (!acked)
            {
                Console.WriteLine($"[venue ] bar {seq}: engine never acked — aborting run");
                break;
            }
        }

        dealer.SendFrame(Wire.Json("goodbye", new { v = 1 }));
        Console.WriteLine("[venue ] done");
    }
}

// ───────────────────────────── the engine ─────────────────────────────

public static class Engine
{
    public static void Run(string endpoint)
    {
        using var router = new RouterSocket();
        router.Bind(endpoint);

        decimal? prevClose = null;
        var inPosition = false;
        var entryPrice = 0m;
        int barsSeen = 0, entries = 0, execs = 0;

        while (true)
        {
            // ROUTER frames: [identity][payload]. The identity names the venue connection,
            // so replies are addressed — multiple venues could share one engine.
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
            if (!inPosition && prevClose is { } p && close > p * 1.0015m)
            {
                commands.Add(new { kind = "open", qty = 1 });
                inPosition = true;
                entryPrice = close;
                entries++;
            }
            else if (inPosition && close < entryPrice * 0.997m)
            {
                commands.Add(new { kind = "close" });
                inPosition = false;
            }
            prevClose = close;

            // The ack that closes the loop: same seq, plus every command this bar produced.
            router.SendMoreFrame(identity).SendFrame(Wire.Json("bar_done", new { seq, commands }));
        }

        Console.WriteLine($"[engine] {barsSeen} bars, {entries} entries, {execs} executions — done");
    }
}

// ───────────────────────────── wire format ─────────────────────────────

public static class Wire
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Json(string type, object body)
    {
        var node = JsonSerializer.SerializeToNode(body, Opts)!.AsObject();
        node.Insert(0, "type", type);
        return node.ToJsonString(Opts);
    }
}
