// PUB/SUB under pressure — the high-water mark.
//
// Every ZeroMQ socket has a per-peer queue with a cap: the high-water mark (default 1,000
// messages each side). What happens when a queue is full depends on the socket type, and
// the difference is the sharpest edge in the library:
//
//   PUSH  blocks the sender  → backpressure, nothing lost
//   PUB   DROPS the message  → the show must go on; a slow subscriber loses data SILENTLY
//
// No exception, no return code, no log line. This demo blasts frames at a deliberately slow
// subscriber and counts both ends. If your events are entitlements (fills, account changes),
// PUB/SUB is the wrong pattern — that lesson is cheaper here than in production.

using NetMQ;
using NetMQ.Sockets;

public static class HighWaterMark
{
    private const int Published = 50_000;
    private const int PayloadBytes = 128;

    public static void Run(int port)
    {
        using var pub = new PublisherSocket();
        pub.Options.SendHighWatermark = 1_000; // the default, made explicit — this is the cliff
        pub.Bind($"tcp://127.0.0.1:{port}");

        using var sub = new SubscriberSocket();
        sub.Options.ReceiveHighWatermark = 1_000;
        sub.Connect($"tcp://127.0.0.1:{port}");
        sub.Subscribe("");

        Thread.Sleep(200); // dodge the slow joiner; today's lesson is a different foot-gun

        var publisher = new Thread(() =>
        {
            var payload = new byte[PayloadBytes];
            for (var i = 0; i < Published; i++)
                pub.SendFrame(payload); // never blocks: full queue means silent drop
        }) { IsBackground = true };
        publisher.Start();

        // The slow consumer: an artificial stall every few messages is all it takes.
        var received = 0;
        while (sub.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(1000), out _))
        {
            received++;
            if (received % 20 == 0) Thread.Sleep(1);
        }
        publisher.Join();

        Console.WriteLine($"published      : {Published:N0} frames of {PayloadBytes} B");
        Console.WriteLine($"received       : {received:N0}");
        Console.WriteLine($"dropped        : {Published - received:N0} — silently, at the high-water mark");
    }
}
