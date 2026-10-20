# ADR-109: Form drafts kept in the browser

Status: accepted

## Decision
While a form has unsaved changes, the Desk keeps a copy in the browser's local storage (after a short pause in typing). Opening the same document again, or the same new-document form, offers the typing back with a note and a **Discard them** button. The draft is removed when the form is saved or when nothing is different any more.

- Nothing is sent to the server, and **Password fields are never kept**.
- A draft is only restored for the same version of the document (its *modified* time); if someone saved it meanwhile, or the draft is more than a week old, it is dropped. Storage that fails (full, blocked, a private window) is ignored.
- It applies only to forms the person can edit. Signing out does not clear local storage, so on a shared computer the usual advice holds: use your own browser profile.

## Consequences
This is a safety net, not auto-save to the server: the document is not changed until the person saves, and there are no server-side drafts or version history for it.
