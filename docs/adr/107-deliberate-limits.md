# ADR-107: Things Qahwa deliberately does not do

Status: accepted

These roadmap items were considered and left out on purpose. Each would add risk or a second way of doing something that already works.

- **System Console** (running code or SQL typed into the Desk): it would bypass every permission, rule and log. Administrators who need to run code have the server scripts sandbox (ADR-070), apps and the CLI, all of which are checked and logged. A SQL box was left out of Query Reports for the same reason.
- **Query profiler (Recorder)**: slow queries are a database and server concern. The database's own tooling (pg_stat_statements, SQL Server Query Store) sees real plans; a profiler inside the app would see less and cost every request. Request and scheduler errors are in Error Log, timed steps in Scheduled Job Log (ADR-075).
- **Session length and password rules as Desk settings**: they stay in server configuration (like the IP allow list, ADR-055). A wrong value typed into the Desk could lock every administrator out with no way back except the database; configuration can be corrected on the server.
- **Job queues (short, default, long)**: the single scheduler already runs safely on several servers (a step is claimed by compare-and-swap) and every step is timed and logged. Separate queues and retries would need a message broker and a second place to look when something fails. Long work belongs in app jobs, which are logged and can be rerun.
