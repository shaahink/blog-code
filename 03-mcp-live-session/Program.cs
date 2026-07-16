// 03 — A live MCP session in .NET: stdio in front, warm state behind.
//
// Distilled from DevContext's MCP server:
// https://github.com/shaahink/DevContext2/blob/feat/tapestry-t4/src/DevContext.Mcp/Program.cs
//
// The server speaks MCP over stdio (what Claude Code / Cursor / most agent hosts spawn) and
// keeps expensive analysis state alive BETWEEN tool calls: `analyze` opens a session and
// returns a handle; every later call answers from the warm snapshot in milliseconds.
// In DevContext the warm state lives in a separate long-lived gRPC server so the desktop app,
// the CLI and the MCP front-end share one graph; here it lives in-process to stay small.
//
// Try it: add to an MCP client config, or poke it by hand —
//   dotnet run --project 03-mcp-live-session
//   then paste: {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe","version":"0"}}}

using McpLiveSession;
using Microsoft.Extensions.DependencyInjection;

// Rule zero of stdio servers: stdout belongs to the protocol. Anything a human should
// see goes to stderr (or a log file) — one stray Console.WriteLine corrupts the stream.
Console.Error.WriteLine("repo-scout MCP server starting (stdio)…");

var services = new ServiceCollection();

services.AddMcpServer(options =>
{
    options.ServerInfo = new() { Name = "repo-scout", Version = "1.0.0" };
})
.WithTools(new RepoTools())
.WithStdioServerTransport();

var provider = services.BuildServiceProvider();
var server = provider.GetRequiredService<ModelContextProtocol.Server.McpServer>();
await server.RunAsync();

Console.Error.WriteLine("repo-scout MCP server stopped");
