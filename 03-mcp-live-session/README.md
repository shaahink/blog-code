# 03 — MCP live session

A stdio MCP server whose tools share warm state across calls: `analyze` opens a session
and returns a handle, `find_files` answers from the snapshot instantly. Stdout carries the
protocol; humans get stderr.

```powershell
dotnet run --project 03-mcp-live-session
```

Or wire it into an MCP client:

```json
{ "mcpServers": { "repo-scout": { "command": "dotnet", "args": ["run", "--project", "03-mcp-live-session"] } } }
```

- Post: [A live MCP session in .NET](https://shaahink.github.io/site/blog/a-live-mcp-session-in-dotnet/)
- Real source: [Program.cs](https://github.com/shaahink/DevContext2/blob/feat/tapestry-t4/src/DevContext.Mcp/Program.cs), [ServerShim.cs](https://github.com/shaahink/DevContext2/blob/feat/tapestry-t4/src/DevContext.Mcp/ServerShim.cs), [DevContextTools.cs](https://github.com/shaahink/DevContext2/blob/feat/tapestry-t4/src/DevContext.Mcp/DevContextTools.cs) in [DevContext](https://github.com/shaahink/DevContext2)
