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
| **ArraySort_Generic** | **Random**    | **100000** | **5.617 ms** | **0.0145 ms** | **0.0121 ms** | **5.629 ms** |  **1.00** |    **0.00** |         **-** |          **NA** |
| Ipnsort           | Random    | 100000 | 5.764 ms | 0.0345 ms | 0.0323 ms | 5.804 ms |  1.03 |    0.01 |         - |          NA |
|                   |           |        |          |           |           |          |       |         |           |             |
| **ArraySort_Generic** | **RandomD20** | **100000** | **5.141 ms** | **0.0543 ms** | **0.0481 ms** | **5.195 ms** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Ipnsort           | RandomD20 | 100000 | 2.139 ms | 0.0290 ms | 0.0272 ms | 2.170 ms |  0.42 |    0.01 |         - |          NA |
|                   |           |        |          |           |           |          |       |         |           |             |
| **ArraySort_Generic** | **RandomS95** | **100000** | **4.035 ms** | **0.0275 ms** | **0.0230 ms** | **4.063 ms** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Ipnsort           | RandomS95 | 100000 | 3.488 ms | 0.0683 ms | 0.0786 ms | 3.621 ms |  0.86 |    0.02 |         - |          NA |
|                   |           |        |          |           |           |          |       |         |           |             |
| **ArraySort_Generic** | **Zipfian**   | **100000** | **5.078 ms** | **0.0063 ms** | **0.0053 ms** | **5.081 ms** |  **1.00** |    **0.00** |         **-** |          **NA** |
| Ipnsort           | Zipfian   | 100000 | 4.113 ms | 0.0299 ms | 0.0280 ms | 4.142 ms |  0.81 |    0.01 |         - |          NA |
