# ADR-074: Permission checks and permission queries for apps

Status: accepted

## Context
Roles, field levels, User Permissions and shares cover most access rules, but a business sometimes needs a rule only code can say: "a clerk sees only tickets of their own region", "nobody may delete a deal over a million". Frappe has `has_permission` and `permission_query_conditions` hooks for this.

## Decision
- An app can register, per DocType:
  - `HasPermission(doctype, check)`: `check(PermissionContext)` gets the DocType, the action (read, write, create, delete, submit, cancel), the document (for a new one, the values being saved) and the person. Returning `false` refuses with "You are not allowed to access this …".
  - `PermissionQuery(doctype, query)`: `query(user)` returns `PermissionFilter(field, values)` conditions ("the field is one of these values") that are added to every list, count, group-by and export of that DocType for that person, so documents they may not open are simply not listed.
- **Apps can only take access away.** The checks run after roles, field levels, User Permissions and shares have allowed the action, so a check returning `true` grants nothing and a role with no right is never helped.
- **Fail closed.** A check that throws refuses the action; a query that throws, names a column the DocType does not have, or names a Password column lists nothing. Each failure is written to the Error Log with the app's name.
- **Safe by scope.** The hooks apply only to the DocTypes people work with (not child tables, single DocTypes or the framework's own DocTypes), so an app cannot lock people out of users, roles or settings, and they do not apply to Administrators (who could undo them anyway). A hook for a DocType that does not exist stops the server at start-up.
- Enforced in `DocumentService` at read, update, submit, cancel, delete, rename, create and in every list-like query (`PrepareListQueryAsync`).

## Consequences
- Checks are synchronous and see only the document and the person (no database access), which keeps them cheap and predictable; a rule that needs other data can precompute it into a field.
- A check and a query for the same rule must agree: the query keeps lists clean, the check protects direct access.
- Reports and exports run through the same list query, so the rule applies there too. Code that reads the database directly (an app's own SQL) is not covered.
