# Benchmarks

`Qahwa.Benchmarks` measures the engine's own cost with BenchmarkDotNet, on the in-memory store, so a change that makes saving or evaluating slower shows up without a database in the way.

```bash
dotnet run -c Release --project benchmarks/Qahwa.Benchmarks -- --filter "*"             # everything
dotnet run -c Release --project benchmarks/Qahwa.Benchmarks -- --filter "*Expression*"  # one class
dotnet run -c Release --project benchmarks/Qahwa.Benchmarks -- --filter "*" --job short # a quicker, rougher run
```

- `ExpressionBenchmarks`: evaluating a rule (arithmetic, a condition, a sum over rows) and parsing one that is not cached.
- `DocumentBenchmarks`: insert, get, update and a list of 50 through `DocumentService` (validation, permissions, hooks).

Always run in Release and compare numbers from the same machine. Load tests of a running server against a real database (the `k6` side of the plan) are not here yet.

## A first run (`--job short`, a loaded laptop, so rough)

| Benchmark | Mean | Allocated |
|---|---|---|
| Expression: arithmetic / condition / sum over rows | 71 / 101 / 125 ns | 120–320 B |
| Expression: parse, not cached | 11.7 µs | 1.6 KB |
| Document: get | 1.2 µs | 3.7 KB |
| Document: list of 50 | 2.3 µs | 5 KB |
| Document: update | 16.6 µs | 24 KB |
| Document: insert | 3.6 ms | 2.4 MB |

The insert figure grows with the number of customers already saved: the in-memory store checks the unique `customer_name` by scanning every row, so it measures that scan more than the engine. Read it as "insert gets slower as the store fills in this harness", not as the cost of an insert against a database.
