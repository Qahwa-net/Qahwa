# ADR-067: Deleted documents can be restored

Status: accepted

## Context
Deleting is permanent. The updates log (ADR-014) records that a document was deleted and a trimmed snapshot (values shortened, dates as text, table rows capped), which is good for an audit but not enough to bring the document back. People delete the wrong thing.

## Decision
- **A faithful copy is kept in the same transaction as the delete.** `DocumentService.DeleteAsync` adds a `Deleted Document` row (`deleted_doctype`, `deleted_name`, the document and all its table rows as JSON) to the writes of the delete, so either both happen or neither. Copies are made for the documents people work with (the same rule as assignment rules: not child rows, single DocTypes or the framework's own DocTypes). Password fields are left out of the copy. A document whose copy would be larger than 1,000,000 characters is deleted without one.
- **Restore** is `POST /api/deleted-documents/{id}/restore` (`DeletedDocumentService.RestoreAsync`). It needs the System Manager role (or Administrator) and the right to create that kind of document. The document comes back under its old name with its old values, owner and dates, and its table rows; it is inserted as it was, not validated again (it was valid when it was deleted, and rules may have changed since). If a document with that name exists now, restoring is refused (409) and the copy stays. The copy is removed once used, and the restored document's history gets a "Restore" entry.
- **Retention:** the scheduler removes copies older than `Qahwa:DeletedDocuments:RetentionDays` (default 30, 1 to 3650), checked hourly. A System Manager can also delete a copy by hand (list and form of Deleted Document).
- The list of what was deleted is the ordinary list of the `Deleted Document` DocType (System Manager: read and delete only, nobody can create or edit one). The Desk form has a **Restore** button.

## Consequences
- The copies hold the data of deleted documents (minus passwords) for the retention period: they are as sensitive as the documents were, and only System Managers can read them.
- Things around the document are not restored: comments, attachments, shares, follows and assignments were removed with it. Links from other documents to it work again because the name is the same.
- Restoring does not run validation, workflows or scripts.
