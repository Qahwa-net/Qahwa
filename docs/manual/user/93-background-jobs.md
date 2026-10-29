# Background jobs (for System Managers)

Apps can hand slow work (sending a batch of messages, building a large file) to a background queue instead of making you wait. Open **Tools → Background Jobs** to see them: *Queued*, *Running*, *Done* or *Failed*, how many attempts were made and the last problem.

A job that fails is tried again after a pause that grows (30 seconds, a minute, two minutes, up to an hour) until it has used its attempts. If it still fails, open a failed job and ask for a retry. Finished jobs are removed after a week (failed ones after a month). Jobs are only read here: you cannot edit them.
