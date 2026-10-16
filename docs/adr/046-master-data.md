# ADR-046: Master data and the automatic "All" role

**Status:** accepted (L10, part 6)

## Context
Almost every business system needs the same reference lists (countries, currencies, languages, salutations, genders), and forms should pick from them instead of taking free text. Everyone who is signed in needs to read them, but no one should be able to change them without a good reason. The permission model had no way to say "everyone signed in".

## Decision
- **Five Core DocTypes:** `Currency` (code, name, symbol, enabled), `Country` (name, ISO code, currency as a link to Currency), `Language` (code, name, right to left), `Salutation`, `Gender`. Other DocTypes link to them like to any DocType (searchable pickers).
- **Seeded by a patch** (ADR-043): `2026_03_01_seed_master_data` runs once at start-up and inserts 244 countries, 152 currencies (from a data file generated from the .NET region data and kept in the repository, so the result never depends on the server's culture settings), 34 languages, 7 salutations and 4 genders. It skips rows that exist, so it never overwrites an edit, and invisible text-direction marks that some systems put around currency symbols are removed. Afterwards they are ordinary documents that System Managers can add to and change.
- **The automatic role "All".** Every signed-in person's session carries the role `All` (also API keys, which stand for a person). It is for what any signed-in person may read, and nothing else: the five lists get `read` for `All` and everything for System Managers. It is added when the session is loaded, never stored and never grants more than rules written for it, and it is not Administrator. The patch also creates a Role document "All" so the Role Permission Manager can use it. **Granting rights to `All` on a sensitive DocType would make it readable by everyone signed in: do that only deliberately.**
- Without a session (no `All` role) nothing is readable.

## Consequences
- Pickers for countries and currencies work for everyone from the first start; the lists show in the sidebar of every user under Core.
- Not yet: states and cities, a phone-code or timezone per country, exchange rates, enabling or disabling countries for forms, translations of the names.
