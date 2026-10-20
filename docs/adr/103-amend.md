# ADR-103: Amending a cancelled document

Status: accepted

## Decision
- A submittable DocType automatically gets a read-only link field `amended_from` (to its own DocType), added when the definition is loaded.
- `POST /api/resource/{doctype}/amend` (`DocumentService.AmendAsync`) turns a **cancelled** document into a new **draft** with the same values and child rows, and sets `amended_from` to the cancelled one. Each cancelled document can be amended once (a second call is a conflict); the new draft can itself be submitted, cancelled and amended again. Passwords, formulas, unique fields and layout fields are not copied (a unique field, such as a number you type, must be filled in again).
- Rights: read the cancelled document, the new **Amend** right (a permission flag like Print and Email: on unless a rule switches it off) and the right to create. The new draft is made as the person, so every ordinary rule applies to it.
- `amended_from` is only ever set by Amend; a value sent by a client when saving is ignored, so nobody can mark another document as amended.
- The Desk shows an **Amend** button on a cancelled document, and the permission editor has an Amend column.

## Consequences
- The new document is named by the DocType's normal naming (not a "-1" suffix of the old name); `amended_from` is the link between them.
- "Select" as a separate permission is not built: reading a list or choosing a document in a Link field already needs only read access.
