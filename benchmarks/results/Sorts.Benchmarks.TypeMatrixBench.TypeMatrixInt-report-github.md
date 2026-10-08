```

BenchmarkDotNet v0.15.4, Windows 11 (10.0.26300.9457)
AMD Ryzen 9 7945HX with Radeon Graphics 2.50GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-NLLXEQ : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Platform=X64  Force=False  Server=False  

```
| Method            | Dist      | N      | Mean       | Error    | StdDev   | P90        | Ratio | Allocated | Alloc Ratio |
|------------------ |---------- |------- |-----------:|---------:|---------:|-----------:|------:|----------:|------------:|
| **ArraySort_Generic** | **Random**    | **100000** | **3,342.8 μs** |  **4.44 μs** |  **4.16 μs** | **3,348.7 μs** |  **1.00** |         **-** |          **NA** |
| Ipnsort           | Random    | 100000 | 1,329.3 μs |  7.31 μs |  5.71 μs | 1,328.5 μs |  0.40 |         - |          NA |
|                   |           |        |            |          |          |            |       |           |             |
| **ArraySort_Generic** | **RandomD20** | **100000** | **1,462.7 μs** |  **0.98 μs** |  **0.91 μs** | **1,464.0 μs** |  **1.00** |         **-** |          **NA** |
| Ipnsort           | RandomD20 | 100000 |   383.4 μs |  2.09 μs |  1.75 μs |   384.9 μs |  0.26 |         - |          NA |
|                   |           |        |            |          |          |            |       |           |             |
| **ArraySort_Generic** | **RandomS95** | **100000** | **1,304.0 μs** |  **8.72 μs** |  **7.28 μs** | **1,314.7 μs** |  **1.00** |         **-** |          **NA** |
| Ipnsort           | RandomS95 | 100000 | 1,250.6 μs |  0.46 μs |  0.41 μs | 1,251.1 μs |  0.96 |         - |          NA |
|                   |           |        |            |          |          |            |       |           |             |
| **ArraySort_Generic** | **Zipfian**   | **100000** | **2,711.0 μs** | **21.96 μs** | **17.14 μs** | **2,709.6 μs** |  **1.00** |         **-** |          **NA** |
| Ipnsort           | Zipfian   | 100000 |   936.5 μs |  0.50 μs |  0.47 μs |   937.0 μs |  0.35 |         - |          NA |
