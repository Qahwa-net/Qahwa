# ADR-129: Importing big files in the background

**Status:** accepted (L10, follows ADR-027 and ADR-128)

## Context
An import you wait for is limited to 5 MB and 5,000 rows, because the request has to finish. A real migration is bigger, and nobody should have to sit in front of a progress bar.

## Decision
- **Background import** (`POST /api/import/{doctype}/background`): the file is checked and the person's rights are checked **at once** (a bad file or a missing right is refused now, not hours later), then it is stored with an **Import Run** and the call returns 202. The limit rises to 50,000 rows and 25 MB. The Desk offers it as "Import in the background", and switches it on by itself for files above 5 MB.
- **The same rules, as the same person.** The scheduler (and an immediate kick when the run is queued) imports the file one document at a time **as the person who asked**, with their roles read fresh, through the same code as an import they wait for: permissions, validation, workflow, rules and child rows (ADR-128) all apply. If that person has been disabled in the meantime the run fails with a clear message and imports nothing.
- **Progress and stopping.** The run records how many documents are done and created, updated and failed every 25 documents, plus the first 100 problems with their lines. The dialog shows a progress bar and can be closed: the import carries on. "Stop the import" stops it after the document it is on (what was imported stays); a run that has not started is cancelled at once.
- **Never run twice.** Importing again after a crash could create everything twice, so a run that stops reporting for an hour is marked **Failed** ("not run again") and its counts say how far it got; the person imports the rest. Runs are claimed with a compare-and-swap, so two servers never import the same file.
- **Limits and privacy.** At most two waiting or running imports per person. The file is deleted from the run the moment it ends (it is held at permission level 9 while it waits, unreadable through the document API); runs are listed for their owner (a System Manager sees all) and removed after a week.

## Consequences
- Not yet: a page listing past imports, importing from a link, an "insert or update" pass, mapping columns by hand, and several imports of one person running side by side.
