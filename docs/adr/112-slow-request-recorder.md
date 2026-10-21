# ADR-112: Slow request recorder

Status: accepted

## Decision
Every API request is timed with one stopwatch. Those that take at least `Qahwa:Recorder:SlowMs` milliseconds (default 1000; 0 turns it off) are kept in a small in-memory list (the last 200), with method, path, status, time taken, who and when. **No query string, body or header is kept**, control characters are removed from the path and it is cut to 200 characters. `GET /api/system/slow-requests` and the **Slow Requests** page (Tools menu) show them, slowest first, to System Managers only. Live-update connections (`/api/realtime`) are not recorded: they are long by design.

## Database statements per request
Each recorded request also says how many statements it sent to the database and how long they took (`DbStats`, counted around the four places where `DocumentRepository` runs a statement). Only counts and time are kept: never the statement text or any value. A request that sends hundreds of statements is the usual cause of a slow page (one query per row), and a request that is slow with few, fast statements points at the application instead of the database.

## Consequences
- It lives in memory on each server (a restart clears it; each server has its own list).
- It points at where to look; it shows how many statements and how long, not the statements. The database's own tools (pg_stat_statements, SQL Server Query Store) see real plans and are the right place to go next. A System Console is still left out (ADR-107).
