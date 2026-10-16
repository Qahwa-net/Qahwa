# ADR-087: Country picker for Phone fields

Status: accepted

## Context
Phone numbers were validated (ADR-079) but a person had to type the country code by hand. "Phone with country" in Frappe offers a country list.

## Decision
- **Country gets a dialling code** (`dial_code`, digits only, e.g. `966`; `1` for the United States and Canada). The master-data seed fills it for all 244 countries, and a one-time patch (`2026_10_country_dial_codes`) adds it to countries seeded earlier without overwriting a code an administrator set.
- **The stored value does not change:** a phone is still text like `+966 50 123 4567` and is checked by the same rule on the server. The Desk's Phone field now shows a country list (name and code, from the Country DocType, readable by everyone) next to the number box; choosing a country writes the `+code` in front. A stored number is split at the **longest** code that starts it (so `+1876…` shows Jamaica, `+1202…` the United States). Numbers without a `+` or with an unknown code show "No code" and are left as typed.
- Countries that share a code (the +1 countries) are told apart by the person's choice, which is kept while the form is open.

## Consequences
- Nothing about the data model of Phone fields changes, so reports, imports and the API are unaffected.
- A code is a prefix guess for display only; it is not a check that the number belongs to the country.
