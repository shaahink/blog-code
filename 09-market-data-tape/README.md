# 09 — market-data tape

Records a synthetic feed into NDJSON shards (deduping duplicate venue events at capture),
replays the tape twice to prove byte-identical journals, then replays it against a
different strategy config — a new backtest with zero new data plumbing.

```powershell
dotnet run --project 09-market-data-tape
```

- Post: [Record the world once: market-data tapes](https://shaahink.github.io/site/blog/record-the-world-once/)
- Real source: [IEventTape.cs](https://github.com/shaahink/Shamshir/blob/main/src/TradingEngine.Domain/Kernel/IEventTape.cs), [TradingEngineCBot.Recorder.cs](https://github.com/shaahink/Shamshir/blob/main/src/TradingEngine.Adapters.CTrader/TradingEngineCBot.Recorder.cs) in [Shamshir](https://github.com/shaahink/Shamshir)
