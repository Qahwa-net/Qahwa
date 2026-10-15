# ADR-009: Submittable documents

**Status:** accepted (L5/L6)

## Context
Documents such as invoices and expense claims must be frozen once approved, and later cancelled rather than edited or deleted. Frappe does this with `docstatus` (0 draft, 1 submitted, 2 cancelled) and separate submit and cancel rights.

## Decision
- **A DocType flag.** `isSubmittable` is set when the DocType is created and cannot change afterwards (turning it off would unlock submitted documents). Child tables cannot be submittable; their rows follow their document.
- **Three states in the existing `docstatus` column.** Drafts edit freely. Submitting (0 to 1) re-validates the document like a save. Cancelling (1 to 2) skips validation, so an old document that fails today's rules can still be cancelled.
- **Locked after submit.** Only fields (and tables) marked `allowOnSubmit` can change in a submitted document; a cancelled document cannot change at all. A submitted document cannot be deleted; a cancelled one can.
- **Two new rights.** `submit` and `cancel` on permission rules, level 0 only, honoured by the Role Permission Manager, the designer and own-only rules. A share never grants them. Both are refused on DocTypes that are not submittable.
- **API.** `POST /api/resource/{doctype}/{name}/submit` and `/cancel`, with an optional `{ "modified": ... }` body for conflict detection. Transitions use the same code path as updates, so permissions, User Permissions, sharing checks and concurrency behave the same.
- **Desk.** A status badge, Submit and Cancel buttons (Submit needs a saved form), locked fields, and "Allow on submit" and Submit/Cancel columns in the designer and the permission manager.

## Consequences
- Not yet: amend (a new draft copied from a cancelled document), a workflow with more states (planned in L8), and linked-document checks on cancel.
