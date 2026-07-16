// backend — a .NET 10 minimal API that streams a (simulated) gated delivery run over SSE
// and takes operator commands over plain POST. The Go TUI in ../tui attaches to this.
//
// Companion to: https://shaahink.github.io/site/blog/a-live-tui-in-the-ai-era/
//
// The interesting part is how little there is: Server-Sent Events are first-class in
// .NET 10 (TypedResults.ServerSentEvents over an IAsyncEnumerable), the broadcast is a
// Channel per subscriber, and commands are boring HTTP — no SignalR, no WebSocket
// handshakes, no client library on the other side beyond "read lines from a socket".

using System.Net.ServerSentEvents;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

var sim = new RunSimulator();
_ = Task.Run(sim.RunLoopAsync); // the fake delivery run, forever in the background

app.MapGet("/", () =>
    "gated-delivery demo backend — GET /run/events (SSE) · POST /run/command {\"action\":\"pause|resume|restart\"}");

// One line of framework: an IAsyncEnumerable<SseItem<T>> becomes a correct
// text/event-stream response, heartbeats and serialization included.
app.MapGet("/run/events", (CancellationToken ct) =>
    TypedResults.ServerSentEvents(sim.Subscribe(ct)));

app.MapPost("/run/command", (Command cmd) =>
    sim.Apply(cmd.Action)
        ? Results.Ok(new { ok = true, action = cmd.Action })
        : Results.BadRequest(new { ok = false, error = $"unknown action '{cmd.Action}'" }));

Console.WriteLine("backend listening on http://127.0.0.1:5058  (Ctrl+C to stop)");
Console.WriteLine("  try: curl -N http://127.0.0.1:5058/run/events");
app.Run("http://127.0.0.1:5058");

public sealed record Command(string Action);
