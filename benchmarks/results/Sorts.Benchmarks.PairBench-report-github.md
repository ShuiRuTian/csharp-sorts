```

BenchmarkDotNet v0.15.4, Windows 11 (10.0.26300.9457)
AMD Ryzen 9 7945HX with Radeon Graphics 2.50GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-NLLXEQ : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Platform=X64  Force=False  Server=False  

```
| Method            | Dist   | N      | Mean     | Error     | StdDev    | P90      | Ratio | Gen0    | Gen1    | Gen2    | Allocated | Alloc Ratio |
|------------------ |------- |------- |---------:|----------:|----------:|---------:|------:|--------:|--------:|--------:|----------:|------------:|
| Linq_OrderBy      | Random | 100000 | 6.031 ms | 0.0322 ms | 0.0301 ms | 6.070 ms |  1.72 | 15.6250 | 15.6250 | 15.6250 | 2400396 B |          NA |
| ArraySort_Generic | Random | 100000 | 3.506 ms | 0.0037 ms | 0.0034 ms | 3.508 ms |  1.00 |       - |       - |       - |         - |          NA |
| Ipnsort           | Random | 100000 | 5.125 ms | 0.0034 ms | 0.0028 ms | 5.127 ms |  1.46 |       - |       - |       - |         - |          NA |
