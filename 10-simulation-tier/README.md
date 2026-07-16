# 10 — simulation tier

A fake venue drives the real engine through a scripted rally-then-crash scenario and the
harness asserts on the exact command sequence: one entry, a drawdown-governor flatten, and
nothing after the lock. No credentials, no market hours — this runs on every push.

```powershell
dotnet run --project 10-simulation-tier
```

- Post: [CI for a trading engine, no broker account required](https://shaahink.github.io/site/blog/ci-without-a-broker-account/)
- Real source: [tests/TradingEngine.Tests.Simulation](https://github.com/shaahink/Shamshir/tree/main/tests/TradingEngine.Tests.Simulation) (FakeCBot, CtraderE2EHarness) in [Shamshir](https://github.com/shaahink/Shamshir)
