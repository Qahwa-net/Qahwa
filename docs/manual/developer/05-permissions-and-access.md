# Permissions and access

Access is decided in layers. A request must pass every layer that applies; nothing a layer denies can be granted by a later one, except that a share can open a single document to someone whose roles do not.

| Layer | Question | Where it is edited |
|---|---|---|
| Module | Is the DocType's module blocked for this user? | Module Profile on the User |
| Role rules | May this role read/write/create/delete (and more) this DocType? Only own documents? | `permissions` in the DocType, or the Role Permission Manager |
| Permission levels | Which fields may the user see and change? | `permLevel` on fields and rules |
| User Permissions | Is the user limited to certain linked values (one territory)? | `User Permission` documents |
| Shares | Was this one document shared with the user? | Share panel on the form |
| Document state | Is it submitted or cancelled (locked)? | Submit / Cancel |

Administrator (the role) passes everything except that nothing is hidden from them either.

## Role rules

A rule is `{ role, read, write, create, delete, export, report, submit, cancel, ifOwner, permLevel }`. A user has the roles given by `Has Role` documents (or a Role Profile). Evaluation for an action takes the best rule across the user's roles: a rule without `ifOwner` gives access to everything, with `ifOwner` only to documents the user owns (`owner`). `create` ignores `ifOwner`.

**Overrides.** The Role Permission Manager (Tools → Role Permissions) stores a replacement rule set per DocType in `__permissions`. It replaces the definition's rules entirely, applies at once, and **Reset** goes back. An empty override means only Administrators. Core DocTypes can only be changed by an Administrator, so nobody locks themselves out of User or Role.

## Permission levels

Every field has a level (default 0). Level 0 is the document's own access. A rule at level N > 0 grants read and/or write on fields at level N and requires the user to already reach the document. Unreadable fields are **absent** from responses (not null), excluded from list columns, and cannot be filtered, sorted or requested. Writes to a field the user cannot write are refused unless the value is unchanged (a form may echo what it loaded). Required checks skip fields the user cannot write. Own-only rules above level 0 only apply when the owner is known (not in lists).

## User Permissions

A `User Permission` (user, allow = a DocType, for value, apply to all DocTypes or one) restricts a user to those values. It restricts the allowed DocType by name, and every DocType that has a Link field to it (empty links stay visible). Lists get the filter, reads/updates/deletes of other documents are refused, and the user cannot set a Link to a value outside their set. Administrators and the `User Permission` DocType itself are exempt.

## Sharing

A `DocShare` (user, DocType, document, can read, can write) grants level-0 read, or write, on one document. It never grants create, delete, submit, cancel or export, and User Permissions still apply. Who may share: Administrators, and people who can write the document through their roles (own-only roles for their own documents); a share cannot be passed on. The person must be an existing user.

## Profiles

- **Role Profile**: a named set of roles. Setting it on a User makes the user's roles exactly the profile's; editing it updates everyone who has it. Choosing or editing a profile needs the right to create `Has Role` documents, otherwise anyone who could edit users could grant any role.
- **Module Profile**: blocked modules. Blocked DocTypes disappear and every call on them is refused.

## Export, report, submit, cancel

Separate rights, level 0 only. Export (CSV) and report need `read` too, go through the normal list rules (so every layer above still applies), exclude password fields, and are capped at 10,000 rows. `submit` and `cancel` apply to submittable DocTypes only.

## History and comments

The updates log and comments need read access to the document (every layer above applies first). History shows each reader only fields they may read, and the `Version` DocType is read-only for everyone, so history cannot be rewritten through the API. Comments: anyone who can read may comment; only the author edits; the author or an Administrator deletes.

## What the API never reveals

- A document you may not read looks like it does not exist where possible (checks run before the document is loaded).
- Hidden fields do not appear in the DocType metadata the Desk receives, and permission rules are not sent to ordinary users.
- Search and user-picker endpoints only list what you may read; the share-user picker is limited to people who may share that document.

## Testing permissions

Use `TestEngine` (`tests/Qahwa.Testing`) with `TestUsers` and set rules with `engine.Overrides.Set("DocType", rules)`. Test both the allowed and the denied path and the list, get, update and delete calls; see `FieldLevelTests`, `UserPermissionTests`, `ShareTests` for patterns.
