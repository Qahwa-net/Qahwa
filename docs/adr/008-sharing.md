# ADR-008: Sharing a document with one person

**Status:** accepted (L5, third part)

## Context
Roles open whole DocTypes. People also need to pass a single document to a colleague who has no role for it ("look at this customer"), without widening anyone's role.

## Decision
- **Shares are documents.** A core DocType `DocShare` (user, DocType, document, can read, can write) holds them, so System Managers can audit them in the Desk. Write implies read.
- **A share adds access, never removes it.** Role access is evaluated first. Where it is missing (no role, or an own-only role and the document is someone else's) a share for that document and user can grant read, or write. It opens level 0 of the document only; fields at higher permission levels still need a rule. User Permissions still apply on top.
- **Where it applies.** Get and update (checked before the document is loaded, so a missing document and a forbidden one look the same), lists (a person with no role sees only what was shared; own-only roles see their own plus shared, using one `OwnerOrNames` filter so paging and totals stay right), and the DocType's meta (a person with only shares can open the form). Create and delete are never granted by a share, and deleting a document removes its shares.
- **Who may share.** Administrators, and people who can edit the document through their roles (own-only roles only for their own documents). A share alone does not let you pass it on. The target must be an existing User. Sharing with neither read nor write removes the share.
- **API and Desk.** `GET/PUT /api/share/{doctype}/{name}`; a Share panel on the form lists shares, toggles "Can edit", stops sharing, and adds people by email.

## Consequences
- Not yet: a separate "share" right, shared-with-me lists in the sidebar, notifications, and sharing with a role or everyone.
- Each protected request with no full role access costs one small extra query for the user's shares.
