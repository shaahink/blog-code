# 02 — ZeroMQ lock-step bridge

A venue process and an engine process joined by a DEALER↔ROUTER pair, where the venue
blocks after every bar until the engine replies `bar_done` with that bar's sequence
number and commands. No bar N+1 until bar N's orders are applied — that ordering
guarantee is what makes runs over the same data reproducible.

```powershell
dotnet run --project 02-zeromq-lock-step
```

- Post: [A deterministic bridge out of cTrader](https://shaahink.github.io/site/blog/a-deterministic-bridge-out-of-ctrader/)
- Real source: [TradingEngineCBot.Events.cs](https://github.com/shaahink/Shamshir/blob/main/src/TradingEngine.Adapters.CTrader/TradingEngineCBot.Events.cs) (venue side), [NetMqMessageTransport.cs](https://github.com/shaahink/Shamshir/blob/main/src/TradingEngine.Infrastructure/Transport/NetMq/NetMqMessageTransport.cs) (engine side) in [Shamshir](https://github.com/shaahink/Shamshir)
