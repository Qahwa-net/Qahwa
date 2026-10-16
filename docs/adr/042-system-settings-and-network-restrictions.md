# ADR-042: System Settings and network restrictions

**Status:** accepted (L10, security part 4)

## Context
Two different needs: a place for site-wide display choices (time zone, date format, first weekday, default language) that a System Manager changes in the Desk, and a way to say "this system may only be used from our networks".

## Decision
- **System Settings** is a Core *single* DocType (one document), editable by System Managers only, with a change log. `GET /api/settings` gives the display part to anyone signed in. Reading never fails: a time zone that does not exist or a language code with symbols falls back to the default instead of breaking every page. The Desk shows dates in the chosen order (`yyyy-mm-dd`, `dd/mm/yyyy`, `mm/dd/yyyy`), moments in the chosen time zone, starts calendar weeks on the chosen day, and uses the default language until a person picks their own.
- **Network restrictions are configuration, not a Desk setting.** `Qahwa:Auth:AllowedIps` lists addresses and CIDR ranges (IPv4 and IPv6, such as `203.0.113.5`, `10.0.0.0/8`, `2001:db8::/32`). Empty means anywhere. Anything else gets 403 on every `/api` path before authentication, rate limiting or any handler looks at the request; the health check, the public web forms and the Desk's static files stay open. This is deliberately **not** editable in the Desk: a wrong entry typed into a screen could lock every administrator out with no way back except the database, while a config file can always be fixed by whoever deploys.
- **Fail closed, fail loud.** An address that cannot be read is refused once a list exists. A malformed entry (`10.0.0.0/99`, a typo) stops the server at start-up with a message naming the setting, rather than quietly opening or closing the system.
- **Real client address.** The check uses the connection's address. Behind a proxy or load balancer, configure ASP.NET Core forwarded headers (and only trust your own proxy) so the real client address is what is checked; the code never reads `X-Forwarded-For` itself.

## Consequences
- Not yet: password and session policy as editable settings (they stay in configuration), per-role or per-user network rules, number and currency formats, scheduled restrictions.
