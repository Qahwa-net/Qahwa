# ADR-119: Escalation of documents that wait too long

**Status:** accepted (L8)

## Context
An approval nobody looks at is as bad as a refusal. A workflow needs a way to say "if this sits for two days, do something".

## Decision
- A **state** can have `escalateAfterHours` (1 to 8760; 0 is off), and what to do then: `escalateNotify` (up to 10 roles, told through the bell) and/or `escalateAction` (the name of a transition leaving the state, done by the system). At least one is needed.
- "Waiting" means the document was **not changed** for that long (its `modified` time). Comments do not reset it. That keeps the rule simple and needs no extra clock on every state entry.
- The scheduler (step "workflow escalation", every tick) finds overdue documents per state, **claims** each by inserting a marker row (`Workflow Escalation`, named from document and state, so a second server gets a conflict and skips it), then writes a system note in the comment thread, tells the roles (only people who may read the document are told, like any notification) and does the action. It escalates **once per wait**: the markers are removed whenever the document moves by a workflow action, so a later wait starts afresh. A failed escalation keeps its marker (it is not retried every few minutes) and is counted in the job log line.
- The system does the action as `system@qahwa` with the Administrator role, so roles and the self-approval rule do not block it, but the transition's condition still applies and an action that needs several people is refused when the workflow is saved.
- Marker rows go with the document when it is deleted or renamed, like other collaboration data.

## Consequences
- Not yet: repeating reminders (every N hours), escalating to the owner's manager, working hours and holidays, an escalation shown on the form.
- A state's escalation is part of the workflow definition, edited in the Workflow Builder (States table, Escalate column).
