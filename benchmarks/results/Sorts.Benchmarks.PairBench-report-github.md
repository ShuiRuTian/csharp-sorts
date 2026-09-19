```

BenchmarkDotNet v0.15.4, macOS 26.6.2 (25G83) [Darwin 25.6.0]
Apple M3 Pro, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  Job-TLUHWT : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Platform=Arm64  Force=False  Server=False  
InvocationCount=64  UnrollFactor=1  

```
| Method            | Dist   | N      | Mean     | Error     | StdDev    | P90      | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------ |------- |------- |---------:|----------:|----------:|---------:|------:|--------:|----------:|------------:|
| Linq_OrderBy      | Random | 100000 | 5.772 ms | 0.1089 ms | 0.1019 ms | 5.859 ms |  1.57 |    0.03 | 2400304 B |          NA |
| ArraySort_Generic | Random | 100000 | 3.682 ms | 0.0281 ms | 0.0234 ms | 3.714 ms |  1.00 |    0.01 |         - |          NA |
| QuadSort          | Random | 100000 | 5.832 ms | 0.0575 ms | 0.0538 ms | 5.888 ms |  1.58 |    0.02 |  800024 B |          NA |
| GlideSort         | Random | 100000 | 8.011 ms | 0.1561 ms | 0.1603 ms | 8.231 ms |  2.18 |    0.04 |  801160 B |          NA |
| DriftSort         | Random | 100000 | 5.280 ms | 0.1044 ms | 0.1161 ms | 5.393 ms |  1.43 |    0.03 |  800024 B |          NA |
