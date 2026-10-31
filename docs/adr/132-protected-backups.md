# ADR-132: Password-protected backups

**Status:** accepted (L12, follows ADR-044)

## Context
A backup holds every document and is copied to folders, drives and mailboxes. ADR-044 left the file unencrypted and said so. A file that leaves the server should not be readable by whoever picks it up.

## Decision
- A backup can be **sealed with a password**. The file (`.qbk`) is `QBK1`, a random salt, the number of key rounds, a random nonce prefix and the piece size, then the zip in **64 KB pieces, each sealed with AES-256-GCM**. The key is PBKDF2-SHA256 of the password (600,000 rounds, a fresh salt per file; a file may not ask for more than 10 million rounds when opened).
- **Tampering is refused, not read back wrong.** Each piece has its own nonce and carries the whole header and an "is this the last piece" flag as authenticated data, so a wrong password, one changed byte, a changed header, two pieces swapped, pieces dropped from the end, or a file cut short all fail with one message ("the password is wrong, or the backup file is damaged"; 400 `wrong_password`). It streams, so a big backup is never held in memory; the plain zip only ever exists in a temporary file that is deleted at once.
- **Passwords:** 12 to 200 characters, no control characters, checked before anything is made. **There is no recovery**: the password is not stored anywhere, so a lost password is a lost backup (the Desk and the CLI say so).
- **Where it is offered.** Desk: "Protect it with a password" on the Backup page, and a password box when a protected file is chosen to restore. API: `POST /api/backup/download-protected` with the password **in the body** and `X-Backup-Password` **as a header** on restore (never in an address, so it does not reach logs or history). CLI: `qahwa backup --out x.qbk --password-stdin` and `qahwa restore x.qbk --password-stdin` (the password is read from stdin, never taken as an argument). Scheduled backups: set `Qahwa:Backup:Password` (preferably through the environment, `Qahwa__Backup__Password`, not a file kept beside the backups) and the daily files become `qahwa-backup-YYYYMMDD.qbk`, pruned among themselves.
- Restoring a protected file without its password asks for it; a password given for a plain file is ignored. Everything else about backups (Administrator only, never with an API key, one at a time, restoring only adds) is unchanged.

## Consequences
- A plain backup is still the default, and the Desk labels it "not encrypted".
- Not yet: encryption of uploaded file contents (they are not in the backup), a key kept in a key store instead of a password, uploading backups to cloud storage.
