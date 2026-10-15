# ADR-010: Role and Module Profiles, export and report rights

**Status:** accepted (completes L5)

## Context
Granting the same roles to many users one by one is slow and error-prone, and some teams should not see whole parts of the system. People who may read a DocType are not automatically people who may take its data away or summarise it.

## Decision
- **Role Profile** is a named set of roles (a core DocType with a child table). Choosing one on a User makes the user's roles exactly the profile's; changing the profile updates everyone who has it. This runs in the engine right after the save, through `ProfileService`.
- **Assigning roles through a profile needs the right to assign roles.** Choosing a profile on a User, or editing a Role Profile, needs permission to create `Has Role` documents. Otherwise anyone who may edit users could hand out any role through a profile.
- **Module Profile** lists blocked modules. A DocType whose `module` is blocked for the user is out of reach: it disappears from the sidebar and every list, read, write, delete, meta, share, rename, export and report call is refused. Administrators are never blocked.
- **Export and report are rights of their own** (`export`, `report` on permission rules, level 0 only), on top of read.
  - Export downloads CSV through the ordinary list rules, so user permissions, own-only rules and field levels still apply. Password fields are never exported, up to 10,000 rows, and cells that start with `=`, `+`, `-`, `@` are prefixed so a spreadsheet cannot run them as formulas.
  - Report gives a count, sum, average, smallest or largest per value of a field, computed from the rows the user may read. Every field name is checked against the DocType's columns and the aggregate against a fixed list.
- Role Profile, Module Profile and their child tables are ordinary core DocTypes, so they are edited in the Desk and appear in the Role Permission Manager.

## Consequences
- Applying a profile replaces roles, as in Frappe; it can remove a role an administrator added by hand.
- The profile is applied after the save in separate statements, so a failure between them leaves the user saved with the old roles; saving again repairs it.
- Not yet: an "import" right, per-DocType module assignment screens, report charts and saved reports (L9).
