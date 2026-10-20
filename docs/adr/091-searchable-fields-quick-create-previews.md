# ADR-091: Searchable fields, creating linked records in place and attachment previews

Status: accepted

## Context
Plain drop-downs were slow to use with long lists (countries, selects), a missing linked record meant leaving the form (and losing unsaved work) to create it, and attachments could only be downloaded.

## Decision
- **Searchable selects.** Select fields (on documents and in child rows) and the country list of a Phone field use `SearchSelect`: type to filter, arrow keys and Enter to choose, a clear button, and the label of the current value stays visible.
- **Create in place.** Link and Dynamic Link fields offer **Create new** when the person may create that DocType (`meta.permissions.create`). `QuickCreate` is a dialog built from the DocType's own metadata and logic (defaults, required fields, permission levels, depends-on rules, child tables, `Fetch from`, formulas); it saves through the normal `POST /api/resource` call, so all server validation, permissions, naming and automation apply, then selects the new document in the original field. The search text becomes the title or name field where one is plain text. Unsaved parent fields and rows stay in place. **Open full form** opens the complete editor in another tab. Nested creation is switched off inside the dialog, and client scripts do not run in it.
- **Attachment previews.** PDFs and raster images open in an in-app viewer from the attachment panel and attachment fields. PDFs are drawn with `pdfjs-dist` on a canvas using a **locally bundled worker** (no remote service, no PDF JavaScript, XFA off, system fonts only), with page navigation and zoom; the file is fetched from the existing protected file endpoint with the person's session, so file permissions are unchanged. Other types still download; damaged or password-protected PDFs and removed images show an error message.
- The document action bar stays at the top of the scrollable content area.

## Consequences
- Adds one front-end dependency (`pdfjs-dist`) and its worker asset to the Desk build.
- The dialog shows only what a form shows to this person: a DocType that needs a calculation or table the dialog cannot fill may fail validation; the message is shown and the full form is one click away.
