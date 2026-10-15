# ADR-020: Assignment rules

**Status:** accepted (L8, part 2)

## Context
Assigning by hand does not scale: every new ticket, lead or request needs someone to look at it and pick a person. A System Manager should be able to say, as data and without code, who gets which documents.

## Decision
- **Rules belong to a DocType**, stored as one JSON row per DocType in `__assignment_rule` (same store, manager, catalog and `modified` conflict check as workflows). Up to 20 rules, tried **in order**; the first active rule whose `condition` holds (an expression over the document's fields; empty means always) and that can find a person assigns the document. Rules cover ordinary DocTypes (not child tables, settings or the framework's own).
- **Strategies.** `RoundRobin` (up to 50 people take turns: whoever the rule gave a document of this DocType to longest ago), `LoadBalancing` (whoever has the fewest open assignments, counted over all their open assignments), `Field` (the user named in a Data, Email or Link-to-User field of the document). Picking is **stateless**: it is worked out from the assignment rows (each automatic assignment records its rule in `ToDo.assignment_rule`), so there is no counter to corrupt and concurrent saves can at worst pick the same person. Disabled or unknown users are skipped.
- **Once per document.** A document is given away by rules once: later saves, or the assignee finishing or someone removing the assignment, never re-assign it. Rules run after every save, so a rule with a condition such as `status == 'Escalated'` starts working when the document reaches that state.
- **Close condition.** When a rule's `closeCondition` becomes true for a document the rule assigned, the open assignment is withdrawn (status Cancelled) and a note says so.
- **What the assignee gets.** The same as a manual assignment: a ToDo with the rule's description, priority and due date (`dueInDays`), a notification, a follow, an activity note naming the rule, and read access if their roles could not open the document (granted directly, `ShareManager.GrantReadAsync`). The person who saved the document is recorded as the one who assigned it.
- **A rule never breaks a save.** The document is saved first; the rule engine runs afterwards and any failure in it (a user disabled meanwhile, a database hiccup) is swallowed, so the person saving never sees an error. The next save tries again.
- **Desk.** An Assignment Rules page (`/assignment-rules`, System Managers): per rule a name, active switch, strategy, people (search by name or email) or field, condition, close condition, description, due days and priority; reorder with the arrows; errors under the rule they belong to.

## Security
Only System Managers manage rules. Rule data is validated on save: conditions parse with the safe expression language (no code) and may only name fields of the DocType, strategies and priorities come from fixed lists, user names must exist, text lengths and counts are limited and control characters refused. Automatic read access is granted only to a user a System Manager named (or whom a document field names), never from client input at assignment time. All values are parameters; rule text is stored as JSON and shown as text. Tests cover hostile condition text, unknown users, disabled users, non-managers, anonymous callers, stale saves and a restart.

## Consequences
- Not yet: weekly working-hours or "on leave" skipping, assigning several people, re-assigning when a field changes, rules by role or team, and a per-rule "Assigned N documents" count.
- Auto repeat, notification rules and email templates are the next L8 parts.
