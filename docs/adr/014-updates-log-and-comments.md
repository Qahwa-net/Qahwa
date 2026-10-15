# ADR-014: Updates log and comments on every document

**Status:** accepted (first part of L7)

## Context
People need to see who changed a document and when, and to talk about it. Both must work for every DocType (including ones made later in the designer or customised) without setup, and the history must be trustworthy.

## Decision
- **The updates log is `Version` rows, on by default.** Every insert, update, submit, cancel, rename, merge and delete of a document writes one row: `(ref_doctype, ref_docname, event, data)`, with the author in `owner` and the time in `creation`. A DocType opts out with `"trackChanges": false` (a checkbox in the designer); child tables and the log itself are never logged (a table change is part of the parent's entry).
- **Same transaction.** The row is passed to the repository as a *side write* of the change (`ChildChanges` for another table; `DeleteAsync` and `RenameAsync` take them too), so the change and its record commit or roll back together. A rejected stale update leaves no entry, and there is no window where history can be missing.
- **What is stored.** For an update, the field-level diff `[field, before, after]` of fields that really changed (values over 2,000 characters are cut, at most 200 fields), plus table rows added, removed and changed. A delete keeps a snapshot of the document and its rows (for a later restore). Password values are never stored. `modified`, `modified_by` and row order are not logged.
- **Reading it** (`GET /api/resource/{doctype}/{name}/timeline`) needs read access to the document, checked first, and shows each reader only what they may read: entries for fields above their permission level are removed, an update that leaves nothing to show is hidden, and the delete snapshot is never returned.
- **Nobody edits history.** `Version` has a read-only permission rule even for System Managers, and the engine writes it directly. Only the framework creates rows.
- **Comments are `Comment` rows** (`reference_doctype`, `reference_name`, `comment_type` Comment or Info, `content`), written through `ActivityService`, not the generic API. Anyone who can read the document may comment (a submitted or cancelled document can still be commented on); content is plain text, 1 to 5,000 characters, no control characters except line breaks and tabs. Only the author edits a comment; the author or an Administrator deletes it. A comment id only works under its own document (no cross-document access by id). Deleting a document deletes its comments; its log stays.
- **System notes.** Sharing, changing a share and stopping a share add an `Info` note to the thread; notes cannot be edited or deleted.
- **Rename and merge** repoint both tables to the new name in the same transaction, so the history follows the document.
- **One timeline.** Versions and comments are merged newest first with `before` paging (30 per page in the Desk, at most 100 per request) and the author's full name.
- **Indexes.** Fields can be marked `indexed`; `ref_docname` and `reference_name` are, so a document's history is found without scanning.
- **Desk.** An Activity panel under every saved form: a comment box (Ctrl+Enter posts), the merged timeline with "Status: Open → Won" lines using field labels, @mentions highlighted, edit and delete on your own comments, and "Load older". Text is rendered as text, never as markup.

## Security
Comment and history reads are gated by document read access (including User Permissions, shares and module blocks); history hides fields by permission level; comment text is never interpreted by the server and is escaped by the Desk; input is length- and character-limited; cross-document comment ids are refused; secrets never enter the log.

## Consequences
- Storage grows with every change; retention (purge by age) and "Deleted Documents" (restore from the snapshot) are not built yet.
- @mentions are only highlighted for now; notifications and following arrive with the notification system (L8).
- Not yet: comment counts in list views, attachments in the thread, a version diff for long text, access/view logs.
