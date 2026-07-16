# 01 — deterministic kernel

One queue, one pure reducer, one journal, one effect executor. Two replays of the same
tape produce byte-identical journals — which is what makes a backtest, the live path,
and the debugger the same program.

```powershell
dotnet run --project 01-deterministic-kernel
```

- Post: [Designing a kernel: one queue, one reducer, one journal](https://shaahink.github.io/site/blog/designing-a-kernel/)
- Real source: [KernelDriver.cs](https://github.com/shaahink/Shamshir/blob/main/src/TradingEngine.Engine/Kernel/KernelDriver.cs), [IKernel.cs](https://github.com/shaahink/Shamshir/blob/main/src/TradingEngine.Domain/Kernel/IKernel.cs) in [Shamshir](https://github.com/shaahink/Shamshir)
