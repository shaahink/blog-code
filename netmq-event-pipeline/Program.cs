// netmq-event-pipeline — event-driven inter-process messaging in .NET with ZeroMQ/NetMQ.
//
// Companion to: https://shaahink.github.io/site/blog/fast-event-driven-ipc-in-dotnet/
//
// Four small demos, each a socket pattern and the lesson that comes with it:
//
//   pipeline    PUSH/PULL fan-out/fan-in — measure real throughput at two payload sizes
//   slowjoiner  PUB/SUB — the first messages are gone before the subscription lands,
//               and the REQ/REP handshake that fixes it
//   hwm         PUB/SUB under pressure — the high-water mark drops messages SILENTLY
//   lockstep    DEALER/ROUTER — a sequence-acked protocol that buys determinism
//
// Everything runs in one process (threads standing in for processes) over real TCP on
// localhost — the sockets don't know the difference, and the demos work unchanged if you
// split the roles into separate processes or machines.
//
// Run all four:  dotnet run
// Run one:       dotnet run -- pipeline | slowjoiner | hwm | lockstep

using NetMQ;

var wanted = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
var all = wanted is "all";

if (all || wanted is "pipeline")
{
    Banner("PUSH/PULL pipeline — fan out, fan in, measure");
    Pipeline.Run(payloadBytes: 16, messages: 200_000, workers: 3, basePort: 15961);
    Pipeline.Run(payloadBytes: 1024, messages: 200_000, workers: 3, basePort: 15971);
}

if (all || wanted is "slowjoiner")
{
    Banner("PUB/SUB — the slow joiner");
    SlowJoiner.Run(port: 15963, syncPort: 15964);
}

if (all || wanted is "hwm")
{
    Banner("PUB/SUB — the high-water mark drops, silently");
    HighWaterMark.Run(port: 15965);
}

if (all || wanted is "lockstep")
{
    Banner("DEALER/ROUTER — lock-step: determinism over sockets");
    LockStep.Run(endpoint: "tcp://127.0.0.1:15967", bars: 40);
}

NetMQConfig.Cleanup(block: false);

static void Banner(string title)
{
    Console.WriteLine();
    Console.WriteLine($"── {title} ─────");
}
