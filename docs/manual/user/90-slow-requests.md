# Slow requests (for System Managers)

Open **Tools → Slow Requests** to see the slowest requests this server has answered since it started, when they took at least one second. Each line shows the address, how long it took, who asked and when. Nothing typed into a request is kept. Each server keeps its own list, and a restart clears it. To see the database queries behind a slow request, use your database's own tools. Administrators can change the limit with `Qahwa:Recorder:SlowMs` (0 turns this off).
