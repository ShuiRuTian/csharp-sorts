```

BenchmarkDotNet v0.15.4, macOS 26.6.2 (25G83) [Darwin 25.6.0]
Apple M3 Pro, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  Job-TLUHWT : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Platform=Arm64  Force=False  Server=False  
InvocationCount=64  UnrollFactor=1  

```
| Method            | Dist      | N      | Mean      | Error     | StdDev    | P90       | Ratio | Allocated | Alloc Ratio |
|------------------ |---------- |------- |----------:|----------:|----------:|----------:|------:|----------:|------------:|
| **ArraySort_Generic** | **Random**    | **100000** | **24.495 ms** | **0.0531 ms** | **0.0470 ms** | **24.557 ms** |  **1.00** |         **-** |          **NA** |
| QuadSort          | Random    | 100000 | 29.271 ms | 0.0632 ms | 0.0560 ms | 29.327 ms |  1.19 |  800024 B |          NA |
| GlideSort         | Random    | 100000 | 33.483 ms | 0.0718 ms | 0.0636 ms | 33.585 ms |  1.37 |  801200 B |          NA |
| DriftSort         | Random    | 100000 | 29.695 ms | 0.1215 ms | 0.1077 ms | 29.823 ms |  1.21 |  800024 B |          NA |
|                   |           |        |           |           |           |           |       |           |             |
| **ArraySort_Generic** | **RandomD20** | **100000** |  **7.497 ms** | **0.0060 ms** | **0.0050 ms** |  **7.503 ms** |  **1.00** |         **-** |          **NA** |
| QuadSort          | RandomD20 | 100000 | 10.322 ms | 0.0378 ms | 0.0354 ms | 10.359 ms |  1.38 |  800024 B |          NA |
| GlideSort         | RandomD20 | 100000 |  8.426 ms | 0.0336 ms | 0.0280 ms |  8.462 ms |  1.12 |  801200 B |          NA |
| DriftSort         | RandomD20 | 100000 |  5.792 ms | 0.0166 ms | 0.0139 ms |  5.803 ms |  0.77 |  800024 B |          NA |
|                   |           |        |           |           |           |           |       |           |             |
| **ArraySort_Generic** | **RandomS95** | **100000** |  **9.400 ms** | **0.0081 ms** | **0.0063 ms** |  **9.407 ms** |  **1.00** |         **-** |          **NA** |
| QuadSort          | RandomS95 | 100000 |  1.877 ms | 0.0223 ms | 0.0209 ms |  1.907 ms |  0.20 |  800024 B |          NA |
| GlideSort         | RandomS95 | 100000 |  2.664 ms | 0.0095 ms | 0.0079 ms |  2.675 ms |  0.28 |  801200 B |          NA |
| DriftSort         | RandomS95 | 100000 |  1.886 ms | 0.0146 ms | 0.0136 ms |  1.906 ms |  0.20 |  800024 B |          NA |
|                   |           |        |           |           |           |           |       |           |             |
| **ArraySort_Generic** | **Zipfian**   | **100000** | **13.439 ms** | **0.0249 ms** | **0.0208 ms** | **13.466 ms** |  **1.00** |         **-** |          **NA** |
| QuadSort          | Zipfian   | 100000 | 14.199 ms | 0.0541 ms | 0.0506 ms | 14.273 ms |  1.06 |  800024 B |          NA |
| GlideSort         | Zipfian   | 100000 | 11.362 ms | 0.0289 ms | 0.0270 ms | 11.396 ms |  0.85 |  801200 B |          NA |
| DriftSort         | Zipfian   | 100000 |  8.412 ms | 0.0362 ms | 0.0338 ms |  8.456 ms |  0.63 |  800024 B |          NA |
