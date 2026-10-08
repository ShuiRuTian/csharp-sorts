```

BenchmarkDotNet v0.15.4, Windows 11 (10.0.26300.9457)
AMD Ryzen 9 7945HX with Radeon Graphics 2.50GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-NLLXEQ : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Platform=X64  Force=False  Server=False  

```
| Method            | Dist      | N      | Mean     | Error     | StdDev    | P90      | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------ |---------- |------- |---------:|----------:|----------:|---------:|------:|--------:|----------:|------------:|
| **ArraySort_Generic** | **Random**    | **100000** | **4.573 ms** | **0.0025 ms** | **0.0020 ms** | **4.574 ms** |  **1.00** |    **0.00** |         **-** |          **NA** |
| Ipnsort           | Random    | 100000 | 6.409 ms | 0.0146 ms | 0.0129 ms | 6.421 ms |  1.40 |    0.00 |         - |          NA |
|                   |           |        |          |           |           |          |       |         |           |             |
| **ArraySort_Generic** | **RandomD20** | **100000** | **1.935 ms** | **0.0286 ms** | **0.0239 ms** | **1.970 ms** |  **1.00** |    **0.02** |         **-** |          **NA** |
| Ipnsort           | RandomD20 | 100000 | 2.578 ms | 0.0104 ms | 0.0097 ms | 2.587 ms |  1.33 |    0.02 |         - |          NA |
|                   |           |        |          |           |           |          |       |         |           |             |
| **ArraySort_Generic** | **RandomS95** | **100000** | **1.681 ms** | **0.0053 ms** | **0.0044 ms** | **1.687 ms** |  **1.00** |    **0.00** |         **-** |          **NA** |
| Ipnsort           | RandomS95 | 100000 | 2.339 ms | 0.0138 ms | 0.0129 ms | 2.358 ms |  1.39 |    0.01 |         - |          NA |
|                   |           |        |          |           |           |          |       |         |           |             |
| **ArraySort_Generic** | **Zipfian**   | **100000** | **3.778 ms** | **0.0116 ms** | **0.0097 ms** | **3.785 ms** |  **1.00** |    **0.00** |         **-** |          **NA** |
| Ipnsort           | Zipfian   | 100000 | 4.992 ms | 0.0991 ms | 0.1453 ms | 5.150 ms |  1.32 |    0.04 |         - |          NA |
