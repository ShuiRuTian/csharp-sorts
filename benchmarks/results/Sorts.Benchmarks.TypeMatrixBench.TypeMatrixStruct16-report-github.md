```

BenchmarkDotNet v0.15.4, Windows 11 (10.0.26300.9457)
AMD Ryzen 9 7945HX with Radeon Graphics 2.50GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-NLLXEQ : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Platform=X64  Force=False  Server=False  

```
| Method            | Dist      | N      | Mean     | Error     | StdDev    | Median   | P90      | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------ |---------- |------- |---------:|----------:|----------:|---------:|---------:|------:|--------:|----------:|------------:|
| **ArraySort_Generic** | **Random**    | **100000** | **4.100 ms** | **0.0364 ms** | **0.0341 ms** | **4.105 ms** | **4.148 ms** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Ipnsort           | Random    | 100000 | 5.883 ms | 0.0552 ms | 0.0516 ms | 5.883 ms | 5.934 ms |  1.43 |    0.02 |         - |          NA |
|                   |           |        |          |           |           |          |          |       |         |           |             |
| **ArraySort_Generic** | **RandomD20** | **100000** | **2.022 ms** | **0.0073 ms** | **0.0061 ms** | **2.021 ms** | **2.029 ms** |  **1.00** |    **0.00** |         **-** |          **NA** |
| Ipnsort           | RandomD20 | 100000 | 1.891 ms | 0.0122 ms | 0.0102 ms | 1.893 ms | 1.903 ms |  0.94 |    0.01 |         - |          NA |
|                   |           |        |          |           |           |          |          |       |         |           |             |
| **ArraySort_Generic** | **RandomS95** | **100000** | **1.796 ms** | **0.0358 ms** | **0.0755 ms** | **1.834 ms** | **1.878 ms** |  **1.00** |    **0.06** |         **-** |          **NA** |
| Ipnsort           | RandomS95 | 100000 | 5.152 ms | 0.0031 ms | 0.0028 ms | 5.153 ms | 5.156 ms |  2.87 |    0.12 |         - |          NA |
|                   |           |        |          |           |           |          |          |       |         |           |             |
| **ArraySort_Generic** | **Zipfian**   | **100000** | **3.233 ms** | **0.0201 ms** | **0.0168 ms** | **3.230 ms** | **3.254 ms** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Ipnsort           | Zipfian   | 100000 | 4.504 ms | 0.0100 ms | 0.0088 ms | 4.508 ms | 4.513 ms |  1.39 |    0.01 |         - |          NA |
