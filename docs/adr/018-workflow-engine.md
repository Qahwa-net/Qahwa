# ADR-018: Workflow engine and Workflow Builder

**Status:** accepted (L8, part 1)

## Context
Business documents rarely go straight from "saved" to "done": an expense is requested, a manager approves it if it is small, a director if it is large. That process must be data a System Manager edits in the Desk, not code, and it must be enforced by the server so no client can skip a step.

## Decision
- **A workflow belongs to one DocType.** It has a `stateField` (a Data or Select field that holds the state), a list of **states** (name, colour, `docStatus`, `editRoles`), a list of **transitions** (`from`, `action`, `to`, `roles`, optional `condition`, `allowSelfApproval`) and an `active` switch. The first state is where new documents start. Limits: 30 states, 100 transitions, 500 characters per condition.
- **Stored as data**, one JSON row per DocType in `__workflow` (same store, manager, catalog and optimistic `modified` check as the Role Permission Manager and Customize Form). It takes effect at once, is reloaded at startup, and is validated against the DocType before saving; errors come back keyed by path (`states[1].name`, `transitions[0].roles`).
- **The server enforces it.** New documents start in the first state whatever the client sends. The state field cannot be edited by hand (a direct write is refused, with a validation error on that field); only a workflow action changes it. Doing an action needs read access and one of the transition's roles (Administrators pass), the transition's `condition` to be true for the document (the same expression language as field logic), and, when `allowSelfApproval` is false, that the person did not create the document. A state may limit who can edit the document while it is in that state (`editRoles`); the action itself does not need write access, so read-only approvers can approve.
- **Documents and `docStatus`.** A state with `docStatus` 1 submits the document, 2 cancels it, and only forward moves are accepted (a workflow cannot "un-submit"). The move goes through the ordinary update, so validation, locking, the updates log and notifications all apply. While a workflow is active, plain Submit and Cancel are refused: the workflow is the only way.
- **Inbox.** `GET /api/workflow/pending` lists documents (across workflows) with an action this person can take now; the Desk shows it on the home page.
- **Desk.** The Workflow Builder (`/workflows`, System Managers): states table, transitions table, active switch, errors under the rows, version-conflict handling. On a form with a workflow, a bar shows the state as a coloured pill and one button per allowed action; the state field is read-only and Submit/Cancel are hidden.

## Security
Only System Managers manage workflows (framework DocTypes: Administrators). The workflow is data validated on save: field names are checked against the DocType, conditions are parsed by the safe expression language (no code), role names and text are cleaned and length-limited, and everything stored is JSON read back through the same validator. Documents are fetched through the permission layers before any action; roles are checked on the server, never trusted from the client; the inbox only lists documents the person may read. Tests cover a forged state on insert and update, wrong roles, failed conditions, self-approval, read-only approvers, concurrent saves, anonymous callers and malformed definitions.

## Consequences
- Not yet: parallel approvals ("2 of 3 must approve"), actions by email link, timeouts and escalation, a state-field helper in the designer (the DocType needs the field first), a diagram view, and a read-only state field on the *new* form (the server ignores what is typed there).
- Assignment rules, auto repeat, notification rules and email templates are the next L8 parts.
