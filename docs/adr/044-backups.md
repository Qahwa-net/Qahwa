# ADR-044: Backups and restore

**Status:** accepted (L12, part 7)

## Context
A system that holds business documents needs a backup people can make and restore without a database administrator, which also works on any database the framework runs on. Database tools (pg_dump) remain the right answer for point-in-time recovery, but they are not available from the Desk and are specific to one database.

## Decision
- **A zip made by the application:** `manifest.json` (format, version, time, the list of tables, the naming-series counters), `bundle.json` (the definitions made in the Desk, as in ADR-032) and one `data/N.jsonl` (a JSON row per line) for every DocType, child tables included. It is **not** a point-in-time snapshot (tables are read one after another); use the database's own tools as well when that matters.
- **What is left out on purpose:** password hashes, two-factor secrets, recovery codes and sessions (they must not be in a file that gets copied around; people set a password again after a restore), uploaded file *contents* (back up the files folder; the File rows are kept), and short-lived or sensitive queues (Email Queue, Webhook Delivery, Error Log, Patch Log).
- **Only an Administrator** can make or restore one (both bypass the rules about who may see or change each document). Not with an API key. One backup or restore at a time (it reads the whole database); a second gets 429.
- **Restoring only adds.** Definitions are applied first through the same validated path as an import (ADR-032), then each row whose name does not exist yet is inserted with its original name, dates and owner; a row that exists, or whose unique value is taken, is left alone; nothing is overwritten or deleted; naming-series counters are only raised. A **preview** (the default) says what would be added. A bad row is counted and reported, never stops the others. Values are converted by the field's own type; columns the DocType does not have are ignored.
- **Hostile files:** the manifest can only name files of the form `data/N.jsonl`; DocTypes must exist and not be on the left-out list; the upload must be `application/zip` (a page on another site cannot send that without a CORS preflight), is limited (`Qahwa:Backup:MaxRestoreMegabytes`, default 1024) both as sent **and as unpacked**, so a small file that expands hugely (a zip bomb) is refused; rows are limited to 1 MB.
- **Download** is built in a temporary file first, so a failure is still a normal error, and sent with `no-store`.
- **Scheduled:** with `Qahwa:Backup:Path` set, the scheduler writes `qahwa-backup-YYYYMMDD.zip` once a day (created exclusively, so with several servers sharing the folder only one makes it), keeps the newest `Qahwa:Backup:Keep` (default 7) and deletes only files of its own name pattern; a half-written `.part` left by a dead server is cleaned up after 6 hours. Failures go to the Error Log.

## Consequences
- A site can be moved or recovered from the Desk and from a folder of daily files.
- Not yet: encryption of the file, including file contents, restoring over existing rows ("overwrite"), uploading backups to cloud storage (S3 and the like), a command line restore.
