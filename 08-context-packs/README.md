# 08 — context packs

Builds an LLM context pack from a toy code graph at two budgets. Sections have their own
budgets, filling is breadth-first from the focus, single bodies are capped, and every cut
is named (`+3 more members`, `+37 lines`) so the reader knows what it didn't get.

```powershell
dotnet run --project 08-context-packs
```

- Post: [A context pack is a knapsack problem](https://shaahink.github.io/site/blog/a-context-pack-is-a-knapsack/)
- Real source: [ContextPackBuilder.cs](https://github.com/shaahink/DevContext2/blob/feat/tapestry-t4/src/DevContext.Core/Graph/ContextPackBuilder.cs) in [DevContext](https://github.com/shaahink/DevContext2)
