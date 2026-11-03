# ADR-137: Engine benchmarks

**Status:** accepted (quality)

## Context
The plan asks for performance checks. A database-backed load test needs a server and a lot of data, but the engine's own cost (validation, permissions, hooks, expression evaluation) can be measured on its own and should not get worse by accident.

## Decision
- `benchmarks/Qahwa.Benchmarks` is a BenchmarkDotNet console project (in the solution, not packable, not run by `dotnet test`). It measures expression evaluation and parsing, and insert, get, update and list through `DocumentService` over the in-memory store from `Qahwa.Testing`.
- Using the in-memory store keeps the numbers about the engine; a change to SQL is covered by the database tests, not here.
- The project runs from the command line (see `benchmarks/README.md`). It is not part of CI, because shared runners give noisy timings; the numbers are for comparing two runs on one machine.

## Consequences
- `benchmarks/k6` holds a k6 load test of a running server (lists, a filter, a deep page, reads and inserts of Notes) with a script that seeds a million rows and one that removes them; a first run is recorded in `benchmarks/README.md`. It needs an API key (a sign-in per virtual user trips the sign-in throttle).
- Not yet: running either set in CI, a stored baseline to compare against, bulk insert, and a larger or separate load generator (the first run shared one laptop with the server and the database).
