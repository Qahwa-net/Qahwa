# ADR-118: Approvals by several people

**Status:** accepted (L8)

## Context
Some steps need more than one person: "two of the directors must approve a large expense". Until now a transition moved the document as soon as one person with a role did it.

## Decision
- A transition has `requiredApprovals` (1 to 10, default 1). With more than one, each **different** person who does the action is recorded in a `Workflow Approval` row (document, state, action, person), and a system note ("ann@example.com approved "Approve" (1 of 2)") goes into the comment thread. The document stays where it is until the last person does it, and then moves through the ordinary update like any other step.
- A person counts once: after they act, the action is no longer offered to them (and is refused if sent). Administrators are not an exception: two approvals are two people. The self-approval rule and conditions apply to every vote.
- The rows are cleared whenever the document moves by any action, so the next time it reaches the state, nobody has approved yet. Deleting or renaming a document removes or follows its rows with the other collaboration data.
- The actions a person sees carry `required` and `given`; the form shows "Approve (1/2)", and the Workflow Builder has a "People needed" column.

## Consequences
- Rows are readable by System Managers only; the form learns how many have approved only through the actions it is offered.
- Not yet: "any N of these roles" mixes (each action counts alone), withdrawing a vote, notifying the others when a vote comes in.
