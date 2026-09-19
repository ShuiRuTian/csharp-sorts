```

BenchmarkDotNet v0.15.4, macOS 26.6.2 (25G83) [Darwin 25.6.0]
Apple M3 Pro, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  Job-TLUHWT : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Platform=Arm64  Force=False  Server=False  
InvocationCount=64  UnrollFactor=1  

```
| Method               | Dist   | N      | Mean     | Error     | StdDev    | P90      | Ratio | RatioSD | Gen0    | Gen1    | Gen2    | Allocated | Alloc Ratio |
|--------------------- |------- |------- |---------:|----------:|----------:|---------:|------:|--------:|--------:|--------:|--------:|----------:|------------:|
| ArraySort_IComparer  | Random | 100000 | 3.994 ms | 0.0219 ms | 0.0183 ms | 4.014 ms |  1.17 |    0.01 |       - |       - |       - |      64 B |          NA |
| ArraySort_Comparison | Random | 100000 | 5.336 ms | 0.0374 ms | 0.0292 ms | 5.363 ms |  1.56 |    0.01 |       - |       - |       - |         - |          NA |
| Linq_OrderBy         | Random | 100000 | 5.742 ms | 0.0984 ms | 0.0872 ms | 5.846 ms |  1.68 |    0.02 | 31.2500 | 31.2500 | 31.2500 | 1600380 B |          NA |
| ArraySort_Generic    | Random | 100000 | 3.428 ms | 0.0114 ms | 0.0095 ms | 3.441 ms |  1.00 |    0.00 |       - |       - |       - |         - |          NA |
| QuadSort             | Random | 100000 | 5.833 ms | 0.1158 ms | 0.1505 ms | 5.974 ms |  1.70 |    0.04 |       - |       - |       - |  400024 B |          NA |
| GlideSort            | Random | 100000 | 9.167 ms | 0.0390 ms | 0.0346 ms | 9.212 ms |  2.67 |    0.01 |       - |       - |       - |  401160 B |          NA |
| DriftSort            | Random | 100000 | 5.044 ms | 0.0986 ms | 0.1476 ms | 5.207 ms |  1.47 |    0.04 |       - |       - |       - |  400024 B |          NA |
