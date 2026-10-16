# ADR-082: Import, Share, Print and Email rights per role

Status: accepted

## Context
Permission rules covered read, write, create, delete, submit, cancel, export and report. Importing from a file, sharing, printing and emailing were allowed to anyone who could read or write, so a role could not be kept from printing (or emailing) documents it could otherwise edit.

## Decision
- A permission rule has four more switches: **Import, Share, Print, Email**. **They default to on**, so every rule written before they existed behaves exactly as before.
- They only take away. Import still needs create (insert) or write (update); sharing and emailing still need the right to change the document; printing still needs read. A switch being off removes the extra right.
- **A person keeps the right if any of their roles' rules for the DocType grants it**; it is removed only when they have rules for the DocType and every one of them has the switch off. People with no rule (for example someone who reached a document through a share) are not affected, and Administrators never are. Only level 0 rules count.
- Enforced on the server (`PermissionService.Allows/Require(meta, user, PermFlag)`) in the data import (preview and import), the sharing manager, `PrintService.RenderAsync` and `EmailService.SendFromDocumentAsync`; the Desk also hides the buttons (`EffectivePermissions` carries the four values).
- The designer's permission table and Role Permission Manager show four more checkboxes.

## Consequences
- Printing is blocked for the document view only; a person who may export can still get data as CSV/Excel (use the export right for that).
- Submit-time printing and email from workflows or rules (system-sent mail) are not affected.
