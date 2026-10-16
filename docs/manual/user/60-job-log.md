# Is the scheduler working? The Scheduled Job Log

Things that run by themselves (queued email, emailed reports, digests, webhooks, backups, repeating documents, rules and the jobs of installed apps) are recorded in **Scheduled Job Log** (Tools menu), newest first.

- **Success** rows say what was done ("3 sent, 0 failed") and how long it took.
- **Failed** rows give the error. The same problem is also in the **Error Log**.
- Nothing is written when a run had nothing to do.
- Rows are kept for 30 days (your administrator can change that).

You need the System Manager role to see it. If you expect a daily job and there is no row for yesterday, check that the scheduler is turned on for the server.
