# Emailed reports

A System Manager can have a saved report emailed on a schedule (**Tools → Emailed Reports**).

1. Choose the **report**, type the **addresses** (one per line, up to 10), and choose **how often** (daily, weekly or monthly) and the **hour** (in UTC).
2. **Rows to send** is how many rows go in the email (up to 200); the email says how many there are in all.
3. **Run as** is the email of an enabled user. The report is run with *that person's* permissions, so the email contains only what they may see. They must be able to open the report.
4. **Schedule**.

For each schedule you see when it will next be sent, when it was last sent, and the last problem, if any. **Send now** sends it straight away, **Pause** and **Resume** stop and restart it, **Remove** deletes it.

Good to know: the email is plain text; if the person it runs as is disabled, nothing is sent and the problem is shown on the schedule; if the server was off for a while, one email is sent when it is back, not one for every missed day.
