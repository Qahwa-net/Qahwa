# ADR-157: DocType Layouts: a simpler form for some roles

**Status:** accepted

## Context
Everyone who may open a DocType sees all its fields, although a clerk may need three of twenty. Frappe's *DocType Layout* lets an administrator make a leaner form for some people.

## Decision
- **A Core DocType `DocType Layout`** (System Managers manage it): a name, the DocType, whether it is enabled, a position, the roles it is for (one per line; none means everyone) and the fields to hide (one per line).
- **The first fitting layout applies.** Enabled layouts for the DocType are sorted by position (then name); the first one whose roles include any of the person's roles (or that names none) is applied to the form definition they are sent: its fields are marked hidden. The Desk already does not draw hidden fields.
- **It is about display, never about permission.** The API, validation, permissions and the field-level rules are unchanged: a hidden field can still be sent and saved, and a person who may not read a field never sees it anyway. For that reason a **required field cannot be hidden**: the layout cannot be saved with one, and it is ignored at apply time if the DocType changed afterwards.
- A layout is checked when saved: the DocType must exist and be a top-level one, and every hidden field must exist.
- *DocType Layout* is listed under **Tools** in the sidebar.

## Consequences
- Tested: hiding for the right roles only, the position rule, a disabled layout, the API still accepting a hidden field, the validation errors and the System Manager restriction. Seen in the running app: a customer form without *Credit limit* for a layout on the Administrator role (the layout and the test customer were deleted afterwards).
- Not yet: reordering fields or sections per layout, per-layout labels, a field picker in the form (the field names are typed one per line), layouts for people who can open a document only through a share, applying a layout to the list's columns.
