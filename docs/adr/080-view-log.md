# ADR-080: View Log ("seen by")

Status: accepted

## Context
For some documents (announcements, policies, approvals) people want to know who has opened them. Frappe has "track views" and a View Log.

## Decision
- A DocType can turn on **track views** (designer checkbox, `trackViews` in the definition; off by default; not for child tables, single DocTypes or framework DocTypes). When a person opens a document through `GetAsync` after every permission check has passed, a `View Log` row per person and document is created or updated: views counted, last viewed time. A forbidden read records nothing.
- **Throttled and safe:** a person's views are counted at most once every 10 minutes, so reloads are not a write storm; a failure to write never stops the read.
- **Visible only to people who may change the document** (`GET /api/resource/{doctype}/{name}/viewers`, also needs read access); the form shows a "Seen by" card to them. Readers cannot see who else looked. System Managers can also list and delete the rows of the View Log DocType.
- Rows are deleted with the document and follow it when it is renamed.

## Consequences
- It records opens through the API, so a script or integration reading the document counts as that person's view.
- It is a convenience log, not an audit trail: use the updates log for changes.
