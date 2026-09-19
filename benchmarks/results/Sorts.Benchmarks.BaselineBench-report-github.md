```

BenchmarkDotNet v0.15.4, macOS 26.6.2 (25G83) [Darwin 25.6.0]
Apple M3 Pro, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  Job-TLUHWT : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Platform=Arm64  Force=False  Server=False  
InvocationCount=64  UnrollFactor=1  

```
| Method               | Dist   | N      | Mean     | Error     | StdDev    | Median   | P90      | Ratio | RatioSD | Gen0    | Gen1    | Gen2    | Allocated | Alloc Ratio |
|--------------------- |------- |------- |---------:|----------:|----------:|---------:|---------:|------:|--------:|--------:|--------:|--------:|----------:|------------:|
| ArraySort_IComparer  | Random | 100000 | 4.122 ms | 0.0824 ms | 0.1282 ms | 4.052 ms | 4.276 ms |  1.13 |    0.04 |       - |       - |       - |      64 B |          NA |
| ArraySort_Comparison | Random | 100000 | 5.557 ms | 0.0926 ms | 0.0866 ms | 5.520 ms | 5.664 ms |  1.52 |    0.04 |       - |       - |       - |         - |          NA |
| Linq_OrderBy         | Random | 100000 | 5.834 ms | 0.0665 ms | 0.0590 ms | 5.837 ms | 5.900 ms |  1.60 |    0.04 | 31.2500 | 31.2500 | 31.2500 | 1600380 B |          NA |
| ArraySort_Generic    | Random | 100000 | 3.647 ms | 0.0617 ms | 0.0845 ms | 3.614 ms | 3.734 ms |  1.00 |    0.03 |       - |       - |       - |         - |          NA |
| QuadSort             | Random | 100000 | 5.784 ms | 0.0667 ms | 0.0557 ms | 5.791 ms | 5.834 ms |  1.59 |    0.04 |       - |       - |       - |  400024 B |          NA |
| GlideSort            | Random | 100000 | 6.309 ms | 0.0466 ms | 0.0413 ms | 6.313 ms | 6.336 ms |  1.73 |    0.04 |       - |       - |       - |  401160 B |          NA |
| DriftSort            | Random | 100000 | 4.930 ms | 0.0359 ms | 0.0280 ms | 4.933 ms | 4.947 ms |  1.35 |    0.03 |       - |       - |       - |  400024 B |          NA |
