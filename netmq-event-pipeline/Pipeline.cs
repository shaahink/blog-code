// PUSH/PULL — the divide-and-conquer pattern.
//
//   ventilator (PUSH, bind) ──► workers ×N (PULL connect → PUSH connect) ──► sink (PULL, bind)
//
// PUSH round-robins across connected workers; PULL at the sink fair-queues them back into
// one stream. No broker, no topic matching — just load-balanced delivery. The measurement
// runs twice from Program.cs: once with 16-byte frames (message-rate bound) and once with
// 1 KB frames (bandwidth starts to matter). The gap between the two runs is the honest
// answer to "how fast is it": the transport is rarely the bottleneck — your payloads are.

using System.Diagnostics;
using NetMQ;
using NetMQ.Sockets;

public static class Pipeline
{
    public static void Run(int payloadBytes, int messages, int workers, int basePort)
    {
        var inbound = $"tcp://127.0.0.1:{basePort}";
        var outbound = $"tcp://127.0.0.1:{basePort + 1}";
        using var done = new CancellationTokenSource();

        var workerThreads = Enumerable.Range(0, workers)
            .Select(i => new Thread(() => Worker(inbound, outbound, done.Token)) { IsBackground = true, Name = $"worker-{i}" })
            .ToArray();
        foreach (var t in workerThreads) t.Start();

        var sinkThread = new Thread(() => Sink(outbound, messages, payloadBytes, done)) { IsBackground = true, Name = "sink" };
        sinkThread.Start();

        // The ventilator: blast every frame as fast as the socket accepts them. PUSH queues
        // per-peer and blocks (rather than drops) when a queue is full — backpressure for free.
        using (var push = new PushSocket())
        {
            // NetMQ's default linger is ZERO: disposing a socket discards anything still
            // queued. Without this line the tail of the run silently vanishes at dispose.
            push.Options.Linger = TimeSpan.FromSeconds(30);
            push.Bind(inbound);
            Thread.Sleep(200); // let the workers connect so the round-robin includes them all

            var payload = new byte[payloadBytes];
            Random.Shared.NextBytes(payload);
            for (var i = 0; i < messages; i++)
                push.SendFrame(payload);
        }

        sinkThread.Join();
        foreach (var t in workerThreads) t.Join();
    }

    private static void Worker(string inbound, string outbound, CancellationToken done)
    {
        using var pull = new PullSocket();
        using var push = new PushSocket();
        push.Options.Linger = TimeSpan.FromSeconds(30);
        pull.Connect(inbound);
        push.Connect(outbound);

        // A worker is a loop with a timeout, not a blocking read — that's what lets it
        // notice shutdown. The "work" here is deliberately nothing; add CPU per message
        // and watch fan-out become the point of the pattern.
        while (!done.IsCancellationRequested)
        {
            if (pull.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(100), out var frame))
                push.SendFrame(frame);
        }
    }

    private static void Sink(string outbound, int expected, int payloadBytes, CancellationTokenSource done)
    {
        using var pull = new PullSocket();
        pull.Bind(outbound);

        var received = 0;
        var sw = new Stopwatch();
        var lastSeen = TimeSpan.Zero;

        while (received < expected)
        {
            if (!pull.TryReceiveFrameBytes(TimeSpan.FromSeconds(10), out _))
                break; // pipeline wedged — bail out rather than hang the demo
            if (received == 0) sw.Start();
            received++;
            lastSeen = sw.Elapsed; // measure to the LAST frame, not to the timeout that ends the loop
        }
        done.Cancel();

        var rate = received / Math.Max(lastSeen.TotalSeconds, 0.0001);
        var mbps = rate * payloadBytes / (1024 * 1024);
        Console.WriteLine(
            $"payload {payloadBytes,5:N0} B : {received:N0} / {expected:N0} msgs through 2 hops in {lastSeen.TotalSeconds:0.00} s" +
            $" — {rate:N0} msg/s ({mbps:N0} MB/s)");
    }
}
