# Scheduled jobs

**Tools → Scheduled Jobs** (System Managers) shows what Qahwa does in the background and how it is going.

- The sentence at the top says how often the scheduler wakes up (every 5 minutes unless your administrator changed it).
- Each row is one job, such as *email queue*, *reminders*, *incoming mail* or *backup*, with what it does, how often it runs, **when it last had work** and how many runs succeeded or failed in the last 24 hours. A job is written to the log only when it had something to do or failed, so "Nothing to do yet" is normal for a quiet job.
- A red **Failed** with a message means the last run went wrong: read the message, and look at the *Error Log* for details.
- The latest log lines are listed below; the whole history (30 days) is in **Scheduled Job Log**.
- If a red notice says the scheduler is turned off on this server, nothing runs there until your administrator turns it back on (`Qahwa:Scheduler:Enabled`).

**Run now**, at the end of a row, runs that job straight away (for example to send the email queue without waiting). It is safe to do while the scheduler is running, and the run is written to the log as "Run by hand".
