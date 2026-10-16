# ADR-065: Rename many documents at once

Status: accepted

## Context
Renaming one document is done (ADR-024): links to it are updated and the name is checked. Cleaning up a code scheme (changing a prefix on fifty documents) one at a time is tedious and error-prone.

## Decision
- **`BulkService.RenameAsync`** and the `"rename"` action of `POST /api/resource/{doctype}/bulk` with `renames: [{ from, to }]` and an optional `merge`. Every pair goes through the ordinary single rename, so the same rules apply: the DocType must allow renaming, the person needs the write right, links are updated, and a name already taken is reported for that document while the others go ahead.
- At most 100 documents; a document may not be listed twice; without `merge`, two documents may not be given the same new name. Each document has its own result (`ok` or the reason), like the other bulk actions.
- **Desk:** a "Rename…" control in the bulk bar (only for types that allow renaming, when the person may write). The person types text to find and text to replace it with; the new names are worked out in the browser (`renamePairs`: plain text, never a pattern, no special replacement codes), an example is shown, a problem (an empty name, too long, two documents with one new name) disables the button, and the server checks everything again.

## Consequences
- Renaming is not atomic across documents: some can succeed while others fail, and the result says which.
- Merging is available through the API only; the Desk never merges by accident.
