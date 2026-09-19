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
| **ArraySort_Generic** | **Random**    | **100000** | **24.429 ms** | **0.0893 ms** | **0.0791 ms** | **24.515 ms** |  **1.00** |         **-** |          **NA** |
| QuadSort          | Random    | 100000 | 29.116 ms | 0.0473 ms | 0.0369 ms | 29.163 ms |  1.19 |  800024 B |          NA |
| GlideSort         | Random    | 100000 | 27.024 ms | 0.0670 ms | 0.0559 ms | 27.118 ms |  1.11 |  801160 B |          NA |
| DriftSort         | Random    | 100000 | 28.820 ms | 0.0422 ms | 0.0394 ms | 28.873 ms |  1.18 |  800024 B |          NA |
|                   |           |        |           |           |           |           |       |           |             |
| **ArraySort_Generic** | **RandomD20** | **100000** |  **7.586 ms** | **0.0141 ms** | **0.0118 ms** |  **7.602 ms** |  **1.00** |         **-** |          **NA** |
| QuadSort          | RandomD20 | 100000 | 10.334 ms | 0.0653 ms | 0.0579 ms | 10.414 ms |  1.36 |  800024 B |          NA |
| GlideSort         | RandomD20 | 100000 |  6.139 ms | 0.0179 ms | 0.0167 ms |  6.158 ms |  0.81 |  801160 B |          NA |
| DriftSort         | RandomD20 | 100000 |  5.774 ms | 0.0195 ms | 0.0183 ms |  5.794 ms |  0.76 |  800024 B |          NA |
|                   |           |        |           |           |           |           |       |           |             |
| **ArraySort_Generic** | **RandomS95** | **100000** |  **9.434 ms** | **0.0197 ms** | **0.0164 ms** |  **9.446 ms** |  **1.00** |         **-** |          **NA** |
| QuadSort          | RandomS95 | 100000 |  1.851 ms | 0.0118 ms | 0.0098 ms |  1.866 ms |  0.20 |  800024 B |          NA |
| GlideSort         | RandomS95 | 100000 |  2.531 ms | 0.0162 ms | 0.0144 ms |  2.555 ms |  0.27 |  801160 B |          NA |
| DriftSort         | RandomS95 | 100000 |  1.833 ms | 0.0248 ms | 0.0232 ms |  1.863 ms |  0.19 |  800024 B |          NA |
|                   |           |        |           |           |           |           |       |           |             |
| **ArraySort_Generic** | **Zipfian**   | **100000** | **13.495 ms** | **0.0730 ms** | **0.0647 ms** | **13.593 ms** |  **1.00** |         **-** |          **NA** |
| QuadSort          | Zipfian   | 100000 | 14.179 ms | 0.0305 ms | 0.0270 ms | 14.212 ms |  1.05 |  800024 B |          NA |
| GlideSort         | Zipfian   | 100000 |  8.764 ms | 0.0149 ms | 0.0125 ms |  8.777 ms |  0.65 |  801160 B |          NA |
| DriftSort         | Zipfian   | 100000 |  8.446 ms | 0.0209 ms | 0.0174 ms |  8.460 ms |  0.63 |  800024 B |          NA |
