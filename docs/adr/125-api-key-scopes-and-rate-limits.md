# ADR-125: API keys limited to some DocTypes, and a rate limit per key

**Status:** accepted (L10, follows ADR-028)

## Context
A key stood for its person with all their rights. A script that only needs to read Customers should not be able to read invoices, change workflows or call anything else its person can, and a key that is stolen or loops should not be able to hammer the server.

## Decision
- **DocType scope.** A key can be limited to up to 20 DocTypes (real, top-level ones; child tables are reached through their parent). A limited key may only use `/api/resource/{doctype}…`, `/api/v1/resource/{doctype}…` and `/api/meta/{doctype}` for those DocTypes. **Every other address is closed to it** (403 "limited to certain DocTypes"): methods, workflows, shares, search, the account, other keys, backups. The check is on the request address, before any endpoint runs, so a new endpoint is closed to limited keys by default rather than open by accident. Names are matched without regard to case against the registry's spelling.
- **Scope only narrows.** It is checked in addition to the owner's own permissions and to the read-only flag; it can never widen anything. An unlimited key behaves exactly as before.
- **Rate limit.** A key can be given a number of requests per minute (1 to 10 000; empty is no limit of its own). Each request that carries the key counts, including refused ones, in one-minute windows; past the limit the answer is **429 with `Retry-After`** and `rate_limited`. The count is per key and kept in memory, so with several servers each one enforces the limit on its own (the effective limit is at most the number of servers times the setting). A signed-in person is not limited by their keys.
- **Choosing.** Both are set when the key is made, on the Account page (a DocType picker and a number); the key list shows "Only Customer" and "3/minute". They cannot be changed afterwards: make a new key and delete the old one.

## Consequences
- Not yet: scopes for methods, reports or single documents, an IP allow-list, a limit shared by several servers, changing a key's limits in place.
