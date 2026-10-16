# ADR-086: Live events across several servers

Status: accepted

## Context
Live updates (ADR-070) are delivered by an in-memory hub, so with several servers behind a load balancer a person only heard changes made on the server their stream was connected to.

## Decision
- **An optional database backplane**, switched on with `Qahwa:Realtime:Backplane = database` (off by default; one server needs nothing). Each server still delivers an event to its own connections immediately, and also queues it; about once a second (`Qahwa:Realtime:PollMilliseconds`, 200 to 10,000) it writes the queued events to the site's **Realtime Event** table and reads the events other servers wrote since its last poll, handing them to its own connections.
- **No double delivery, right order.** Each row names its origin server (a random id per process), so a server skips its own rows. Row names are series numbers that sort in publishing order, and a server reads "names greater than the last one it saw".
- **Nothing private.** An event carries only its type, the person, the DocType and document name and whether it was a delete, the same as on the stream. Rows are removed after two minutes.
- **A server that has just started** only notes where the table ends; it does not replay old events.
- **Failure is quiet.** If the database cannot be reached, a warning is logged once and live events stay on the server until it can; nothing else is affected. The write queue holds at most 1,000 events (the oldest are dropped).
- It is per site (each site has its own table in its own database) and works on PostgreSQL and SQL Server.

## Consequences
- Latency across servers is about the polling interval. Each server reads the table once per interval, so a database is a fine backplane for tens of servers; a message broker would be better above that.
- Every saved document and new notification becomes one small row while the backplane is on.
