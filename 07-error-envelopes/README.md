# 07 — error envelopes

Symbol resolution for an agent-facing tool: exact unique matches resolve silently,
everything else returns a `{error, hint, example, candidates}` envelope that fits in
80 tokens. The budget is enforced in code, and ambiguity is never resolved by guessing.

```powershell
dotnet run --project 07-error-envelopes
```

- Post: [Fail in 80 tokens: error design for agent-facing tools](https://shaahink.github.io/site/blog/fail-in-80-tokens/)
- Real source: [DevContextTools.cs](https://github.com/shaahink/DevContext2/blob/feat/tapestry-t4/src/DevContext.Mcp/DevContextTools.cs) in [DevContext](https://github.com/shaahink/DevContext2)
