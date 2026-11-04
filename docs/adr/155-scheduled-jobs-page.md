# ADR-155: A Scheduled Jobs page

**Status:** accepted

## Context
The scheduler runs seventeen steps and logs what they did, but the only way to see that was the raw *Scheduled Job Log* list. Frappe has a Scheduled Job Type list showing each job, when it last ran and a way to read its log.

## Decision
- **A catalog of the steps** (`SchedulerJobs.All`: name, a plain description, and its pace: every cycle or hourly) kept next to the scheduler. A job found in the log that is not in the catalog (an installed app's own job) is listed too, as "its own schedule".
- **An overview** (`JobLogService.OverviewAsync`): for each job, its last logged run (status, message, time, duration) and how many runs succeeded or failed in the last 24 hours. Steps are logged only when they had something to do or failed, so "last run" means the last time a job had work; the page says so.
- **`GET /api/scheduler`** (System Managers and Administrators) returns whether the scheduler is on, its interval, the overview and the 30 newest log lines. **Scheduled Jobs** is a *Tools* entry in the sidebar and opens the page, which refreshes every 30 seconds and links to the full *Scheduled Job Log*.

## Consequences
- Tested: the overview lists every known job, keeps the newest row as "last", counts the day, adds app jobs and shows jobs with nothing yet; the endpoint is for System Managers only. Seen in the running app against the dev database (the real log rows showed).
- Not yet: running a job on demand from the page (the steps are closures inside the scheduler, not separately callable), pausing a job, per-job pace settings.
