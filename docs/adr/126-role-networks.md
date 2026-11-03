# ADR-126: Limiting a role to some networks

**Status:** accepted (L10, follows ADR-042)

## Context
The site-wide allow-list (`Qahwa:Auth:AllowedIps`, ADR-042) is all-or-nothing and lives in configuration. Organisations also want "finance may only work from the office network" without touching everyone else, and without a server restart.

## Decision
- A **Role** has an "Allowed networks" field (addresses and ranges, one per line, for example `10.0.0.0/8`). Empty means anywhere. It is edited on the Role form by a System Manager, like the rest of the role.
- A person who holds a restricted role is **refused (403)** from every other address, on every request, whether they came with a session or an API key; the message names the role. Someone with several restricted roles must be inside the networks of **all** of them. The Administrator role is never blocked.
- **No lock-out by typing.** `Administrator`, `System Manager` and the automatic `All` role cannot be restricted (the Role form refuses it with the reason), and the address list is checked when saved (a typo is refused, never half-applied). The break-glass path is therefore always a System Manager who is not restricted, plus the server configuration; this is why the site-wide list itself stays in configuration (ADR-107).
- The address is the connection's address as the server sees it (so behind a reverse proxy the forwarded-headers setup of the deployment notes must be right, as for login rate limiting).
- The rules are read from the Role documents and kept for 15 seconds, so a change applies within seconds on every server; on the server that saved it, at once if asked to refresh.

## Consequences
- Not yet: rules per person, time-of-day rules, a list of recent refusals, and a lock-out preview ("who would be blocked from here").
