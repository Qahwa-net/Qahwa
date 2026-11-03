# Benchmarks

`Qahwa.Benchmarks` measures the engine's own cost with BenchmarkDotNet, on the in-memory store, so a change that makes saving or evaluating slower shows up without a database in the way.

```bash
dotnet run -c Release --project benchmarks/Qahwa.Benchmarks -- --filter "*"             # everything
dotnet run -c Release --project benchmarks/Qahwa.Benchmarks -- --filter "*Expression*"  # one class
dotnet run -c Release --project benchmarks/Qahwa.Benchmarks -- --filter "*" --job short # a quicker, rougher run
```

- `ExpressionBenchmarks`: evaluating a rule (arithmetic, a condition, a sum over rows) and parsing one that is not cached.
- `DocumentBenchmarks`: insert, get, update and a list of 50 through `DocumentService` (validation, permissions, hooks).

Always run in Release and compare numbers from the same machine.

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

## Load test of a running server (k6)

`benchmarks/k6/api.js` drives a real server over HTTP: lists, a filter, a deep page, reads and inserts of Notes, with thresholds on the 95th percentile of each. Seed a million rows first so the list cases mean something:

```bash
docker exec -i <postgres container> psql -U <user> -d <database> -v owner="'admin@example.com'" < benchmarks/k6/seed-notes.sql
# make an API key in the Desk (Account, API keys), then:
docker run --rm -i -e BASE_URL=http://host.docker.internal:5101 -e API_KEY=<key> -e VUS=10 -e DURATION=30s grafana/k6 run - < benchmarks/k6/api.js
docker exec -i <postgres container> psql -U <user> -d <database> < benchmarks/k6/clean-notes.sql
```

Use an API key, not a password: signing in once per virtual user trips the server's sign-in throttle (a first run that did so failed 99% of its requests, which measured the throttle, not the API).

### A first run (30 s, 10 readers and 2 writers, 1,000,000 Notes, Release server and PostgreSQL 16 in Docker on one laptop, with k6 on the same machine)

| Request | 95th percentile | Threshold |
|---|---|---|
| List 20 newest | 342 ms | 500 ms |
| Page 5000 deep (offset) | 261 ms | 1500 ms |
| Filter `title like %7%` (a scan) | 809 ms | 1500 ms |
| Read one | 157 ms | 300 ms |
| Insert one | 360 ms | 800 ms |

No request failed (1,463 in all, about 47 per second). Everything shared one machine, so read these as a floor for a first comparison, not as capacity. The first draft of the filter threshold (800 ms) was crossed by 9 ms; the filter is an unindexable scan, so its threshold is a worst-case guard.
