// PUB/SUB — the slow joiner.
//
// A subscription is itself a message: it travels from SUB to PUB after `Connect`, and until
// it arrives the publisher filters everything out at the sending side. So a publisher that
// starts blasting immediately WILL lose its first events to every subscriber — this is the
// single most-reported "bug" in ZeroMQ, and it's by design (PUB/SUB is a radio broadcast,
// not a mailbox).
//
// Run 1: naive — subscribe, then count what actually arrives.
// Run 2: synchronised — the subscriber says "ready" over a REQ/REP pair first, and the
//        publisher holds fire until it hears it.

using NetMQ;
using NetMQ.Sockets;

public static class SlowJoiner
{
    private const int Events = 1_000;

    public static void Run(int port, int syncPort)
    {
        Console.WriteLine($"naive start    : received {Naive(port),4:N0} of {Events:N0} events" +
                          " — the head of the stream is simply gone");
        Console.WriteLine($"handshake first: received {Synchronised(port + 10, syncPort),4:N0} of {Events:N0} events");
    }

    private static int Naive(int port)
    {
        using var pub = new PublisherSocket();
        pub.Options.SendHighWatermark = 10_000; // HWM raised: today's lesson is a different foot-gun
        pub.Bind($"tcp://127.0.0.1:{port}");

        // Publisher starts the moment it can — no one is listening yet, and PUB doesn't care.
        var publisher = new Thread(() =>
        {
            for (var i = 0; i < Events; i++)
                pub.SendMoreFrame("tick").SendFrame(i.ToString());
        }) { IsBackground = true };
        publisher.Start();

        using var sub = new SubscriberSocket();
        sub.Options.ReceiveHighWatermark = 10_000;
        sub.Connect($"tcp://127.0.0.1:{port}");
        sub.Subscribe("tick");

        var count = CountUntilQuiet(sub);
        publisher.Join();
        return count;
    }

    private static int Synchronised(int port, int syncPort)
    {
        using var pub = new PublisherSocket();
        using var rep = new ResponseSocket();
        pub.Options.SendHighWatermark = 10_000;
        pub.Bind($"tcp://127.0.0.1:{port}");
        rep.Bind($"tcp://127.0.0.1:{syncPort}");

        var publisher = new Thread(() =>
        {
            rep.ReceiveFrameString(); // block until the subscriber says it's ready
            rep.SendFrame("go");
            Thread.Sleep(100);        // one more beat: the REP reply can outrun the subscription
            for (var i = 0; i < Events; i++)
                pub.SendMoreFrame("tick").SendFrame(i.ToString());
        }) { IsBackground = true };
        publisher.Start();

        using var sub = new SubscriberSocket();
        using var req = new RequestSocket();
        sub.Options.ReceiveHighWatermark = 10_000;
        sub.Connect($"tcp://127.0.0.1:{port}");
        sub.Subscribe("tick");
        req.Connect($"tcp://127.0.0.1:{syncPort}");
        req.SendFrame("ready");
        req.ReceiveFrameString();

        var count = CountUntilQuiet(sub);
        publisher.Join();
        return count;
    }

    private static int CountUntilQuiet(SubscriberSocket sub)
    {
        var count = 0;
        while (sub.TryReceiveFrameString(TimeSpan.FromMilliseconds(800), out _))
        {
            sub.ReceiveFrameString(); // payload frame of the [topic][payload] pair
            count++;
        }
        return count;
    }
}
