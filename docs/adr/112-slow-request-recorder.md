# ADR-112: Slow request recorder

Status: accepted

## Decision
Every API request is timed with one stopwatch. Those that take at least `Qahwa:Recorder:SlowMs` milliseconds (default 1000; 0 turns it off) are kept in a small in-memory list (the last 200), with method, path, status, time taken, who and when. **No query string, body or header is kept**, control characters are removed from the path and it is cut to 200 characters. `GET /api/system/slow-requests` and the **Slow Requests** page (Tools menu) show them, slowest first, to System Managers only. Live-update connections (`/api/realtime`) are not recorded: they are long by design.

## Consequences
- It lives in memory on each server (a restart clears it; each server has its own list).
- It points at where to look; it does not show queries. The database's own tools (pg_stat_statements, SQL Server Query Store) see real plans and are the right place to go next. A System Console is still left out (ADR-107).
