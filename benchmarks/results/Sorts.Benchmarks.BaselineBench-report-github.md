```

BenchmarkDotNet v0.15.4, Windows 11 (10.0.26300.9457)
AMD Ryzen 9 7945HX with Radeon Graphics 2.50GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-NLLXEQ : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Platform=X64  Force=False  Server=False  

```
| Method               | Dist   | N      | Mean     | Error     | StdDev    | P90      | Ratio | Gen0    | Gen1    | Gen2    | Allocated | Alloc Ratio |
|--------------------- |------- |------- |---------:|----------:|----------:|---------:|------:|--------:|--------:|--------:|----------:|------------:|
| ArraySort_IComparer  | Random | 100000 | 4.054 ms | 0.0061 ms | 0.0058 ms | 4.059 ms |  1.20 |       - |       - |       - |      64 B |          NA |
| ArraySort_Comparison | Random | 100000 | 5.656 ms | 0.0114 ms | 0.0101 ms | 5.672 ms |  1.68 |       - |       - |       - |         - |          NA |
| Linq_OrderBy         | Random | 100000 | 5.951 ms | 0.0389 ms | 0.0304 ms | 5.988 ms |  1.76 | 23.4375 | 23.4375 | 23.4375 | 1600466 B |          NA |
| ArraySort_Generic    | Random | 100000 | 3.375 ms | 0.0029 ms | 0.0024 ms | 3.377 ms |  1.00 |       - |       - |       - |         - |          NA |
| Ipnsort              | Random | 100000 | 1.219 ms | 0.0012 ms | 0.0011 ms | 1.220 ms |  0.36 |       - |       - |       - |         - |          NA |
