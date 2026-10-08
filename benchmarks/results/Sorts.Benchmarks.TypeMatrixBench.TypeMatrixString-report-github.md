```

BenchmarkDotNet v0.15.4, Windows 11 (10.0.26300.9457)
AMD Ryzen 9 7945HX with Radeon Graphics 2.50GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-NLLXEQ : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Platform=X64  Force=False  Server=False  

```
| Method            | Dist      | N      | Mean      | Error     | StdDev    | P90       | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------ |---------- |------- |----------:|----------:|----------:|----------:|------:|--------:|----------:|------------:|
| **ArraySort_Generic** | **Random**    | **100000** | **20.147 ms** | **0.1344 ms** | **0.1050 ms** | **20.273 ms** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Ipnsort           | Random    | 100000 | 22.248 ms | 0.4346 ms | 0.5651 ms | 22.939 ms |  1.10 |    0.03 |         - |          NA |
|                   |           |        |           |           |           |           |       |         |           |             |
| **ArraySort_Generic** | **RandomD20** | **100000** | **12.839 ms** | **0.1386 ms** | **0.1229 ms** | **12.929 ms** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Ipnsort           | RandomD20 | 100000 |  8.220 ms | 0.1386 ms | 0.1423 ms |  8.386 ms |  0.64 |    0.01 |         - |          NA |
|                   |           |        |           |           |           |           |       |         |           |             |
| **ArraySort_Generic** | **RandomS95** | **100000** | **13.913 ms** | **0.1703 ms** | **0.1509 ms** | **14.119 ms** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Ipnsort           | RandomS95 | 100000 | 18.587 ms | 0.3259 ms | 0.3048 ms | 18.916 ms |  1.34 |    0.03 |         - |          NA |
|                   |           |        |           |           |           |           |       |         |           |             |
| **ArraySort_Generic** | **Zipfian**   | **100000** | **18.868 ms** | **0.3354 ms** | **0.3137 ms** | **19.273 ms** |  **1.00** |    **0.02** |         **-** |          **NA** |
| Ipnsort           | Zipfian   | 100000 | 17.504 ms | 0.3368 ms | 0.2813 ms | 17.685 ms |  0.93 |    0.02 |         - |          NA |
