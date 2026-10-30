# ADR-122: Formulas that read a linked document

**Status:** accepted (L4)

## Context
"Fetch from" copies a value once, when the link changes, and cannot be used in a calculation. Forms need "credit limit minus this order" or "refuse a discount above what the customer's tier allows", which means reading another document inside a formula or rule.

## Decision
- One function: `lookup(link_field, 'field')`. The first argument is a **bare Link field name** of the record (or of the table row), the second a **text literal**; anything else is refused when the expression is parsed, in both engines. So the document and field a formula reads are known before it runs: no computed document names, no loops, no way to wander.
- The language stays pure. Before evaluating, the server (and the Desk) fetches the documents the record's Link fields point at, **only for Link fields an expression actually looks up**, and hands them to the evaluator as a `Linked` map. `lookup` just reads from it. DocTypes that do not use it pay nothing.
- **Permissions are the person's own.** The server reads each linked document through the same path as opening it (role, owner, user-permission, share and permission-level rules, "hidden" fields removed), without leaving a "seen by" trace. A document or field the person may not read is simply absent, so `lookup` gives no value; a rule cannot be used to probe a value they may not see unless the rule's author wrote it so. Password fields are never readable. This is stricter than "fetch from" (which checks only DocType-level read).
- Checked when the DocType is saved: the first argument must be a Link field to a DocType, and the field must exist there (and not be a Password field).
- The Desk fetches the same documents through `GET /api/lookup/{doctype}/{name}` as a Link field is chosen, so formulas, conditions and rules work out live while typing and the server checks the same thing on save (same shared test vectors, with a `linked` map).
- Works in formulas, expression defaults, show/require/read-only conditions and validation rules, on the document and its rows.

## Consequences
- Not in workflow transition conditions (a workflow condition is evaluated without linked documents, so a `lookup` there gives no value) and not across more than one hop (`lookup` of a lookup): add a "fetch from" field for those.
- A calculated value from `lookup` is a snapshot taken at save: it does not change when the other document does. Open and save the document again to refresh it.
- Not yet: reading many documents (sums over another DocType).
