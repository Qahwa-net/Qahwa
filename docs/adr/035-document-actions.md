# ADR-035: Document actions (no-code automation)

**Status:** accepted (L11, part 1)

## Context
People want "when an order is approved, create a delivery" or "when a ticket is opened, set its status" without a developer. Scripting would give that, but a scripting language in a business system is also a place for security and performance trouble. The framework already has defaults, formulas, fetch-from, validation rules, workflow, assignment, notifications and auto repeat; what is missing is a small set of safe actions.

## Decision
- **Actions are data.** The Core DocType `Document Action` (title, active, DocType, event, watched field, condition, action, targets) is edited by System Managers in the ordinary Desk form.
- **Events:** New, Update, ValueChange (a named field changed), Submit, Cancel, with the same condition language as notification rules.
- **Two actions only:** *Set Field* (set one plain field of the same document) and *Create Document* (create a document of another DocType from "field = text" lines). Values are text with `{{field}}` placeholders (plus name, doctype, user). There is no code, no expression that reaches beyond the document, no HTTP call, no way to read other documents.
- **No privilege escalation.** An action runs with the rights of the **person whose change started it**. A System Manager cannot use an action to write where they, or the person making the change, could not. If the person may not do it, the action fails and says so.
- **No loops.** A change made by an action never starts further actions (an ambient flag), so actions cannot trigger each other or themselves. Other hooks (notifications, webhooks, assignment) still see the change. At most 20 actions run per save.
- **Checked every time.** The action is validated each time it runs (DocType matches, plain target fields only: no passwords, attachments, tables, calculated fields; target DocType not a framework DocType, child table or single; placeholders are plain field names). A broken action does nothing and records its problem in `last_error`; it never fails the save. Successful runs are counted.
- **Cache.** The active actions are cached for 15 seconds and dropped at once when an action is saved or deleted on the same server.

## Consequences
- Common automation needs no developer and has a small, reviewable surface.
- Not yet: actions on a schedule or after a delay, updating other documents, sending emails or webhooks from an action (notification rules and webhooks cover those), several actions in order inside one rule, a dedicated builder page (the ordinary form is used), a server script sandbox.
