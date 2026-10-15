# ADR-006: Permission levels and the Role Permission Manager

**Status:** accepted (L5, first part)

## Context
A DocType's permission rules were fixed in its definition: all-or-nothing access to whole documents, changeable only by editing the definition. Low-code needs two more things: hiding some fields (a salary, a internal note) from people who may otherwise use the document, and changing who may do what without redesigning the DocType.

## Decision
- **Permission levels (0–9).** Every field and every rule has a level (default 0). Level 0 rules are the document's access (read, write, create, delete, own-only). A rule at level N > 0 grants read and/or write on the fields at level N, and only to a person who can already read the document. Create and delete only exist at level 0. Child-table fields have no levels of their own; rows follow their document.
- **The server enforces it.** Unreadable fields are removed from the response (absent, not null), from list columns, and filters, sorting and field requests on them are refused so values cannot be inferred. Writes to unwritable fields are rejected unless they only echo the existing value (so a form can send back what it loaded), and required checks skip fields the person cannot write. Administrator sees and writes everything.
- **Own-only at level > 0** only applies when the document's owner is known. A list has no single owner, so such rules grant nothing there, which errs on the side of hiding.
- **Overrides, not edits.** The Role Permission Manager stores a replacement rule set per DocType in `__permissions` (Custom DocPerm in Frappe). It replaces the definition's rules entirely, applies at once (an in-memory snapshot, loaded at start-up), can be reset to the definition, and is guarded by optimistic concurrency on its modified time. An empty override means only Administrators may use the DocType.
- **Who manages.** System Managers and Administrators; framework (Core) DocTypes can only be changed by an Administrator, so nobody can lock themselves out of User, Role or Has Role by mistake. Child tables have no permissions of their own and are not listed.
- **The Desk follows, the server decides.** `/api/meta/{doctype}` returns only the fields the person may read and the levels they may write; the form makes other fields read-only and does not send them. The designer gets a level on each field and rules, and the Role Permissions page edits the matrix.

## Consequences
- Rules can be wrong in new ways (a level with no readers hides a field from everybody but Administrators); the page shows which fields each level protects.
- Formulas that read a hidden field cannot be calculated in the Desk for that person; the server still calculates them on save.
- Still to do in L5: User Permissions (restrict by linked value), sharing, submit/cancel/amend/report/export rights, Role and Module Profiles.
