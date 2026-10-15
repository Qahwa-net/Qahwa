# ADR-007: User Permissions (restrict by linked value)

**Status:** accepted (L5, second part)

## Context
Roles say what a person may do with a DocType, not which documents. "Ali only works with the North territory" is a common need and should not require a new role or code.

## Decision
- **Rows are documents.** A core DocType `User Permission` (user, allow = DocType name, for_value, apply to all DocTypes or only one) holds the restrictions, so they are created and audited in the Desk like anything else. Only System Managers (and Administrators) can manage them.
- **What a restriction does for a user.** For each DocType D, rows whose `allow` is D restrict D itself by name; rows whose `allow` is the target of a Link field in D restrict that field. Link fields may stay empty (so documents with no link remain visible); a document's own name may not.
- **Enforced in `DocumentService`.** Lists get an extra filter (`IN`, or `IN ... OR NULL` for links), so totals and paging stay correct. Reading, updating or deleting a document outside the restriction is refused, and a restricted user cannot set a Link field to a value they are not allowed (a field error). Link search and autocomplete use the same list call, so they only offer allowed values.
- **Administrators are never restricted**, and `User Permission` itself is exempt, so nobody can lock out the person who manages the rules.
- **No cache.** The user's rows are read on each request (one small indexed query), so changes apply at once and nothing can go stale.

## Consequences
- Not yet: "default" values, restrictions through Dynamic Link fields or link fields inside child tables, ignoring restrictions per DocType, hiding restricted documents from `fetchFrom`, and a friendlier editor for the rule (the DocType name is typed, not picked).
- Creating a document of the restricted DocType itself is not blocked, because its name is not known until it is saved; the creator may not see it afterwards unless allowed.
