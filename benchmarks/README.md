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
