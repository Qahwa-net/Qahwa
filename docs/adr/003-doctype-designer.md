# ADR-003: DocType Designer: definitions as data, applied at runtime

**Status:** accepted (L1)

## Context
The goal is a low-code framework: a user should be able to create a new kind of document in the Desk without editing files or restarting the server.
Until now DocTypes came only from JSON files (and the framework's embedded ones), loaded once at startup.

## Decision
- A DocType has a **source**: `Core` (embedded in the framework), `App` (JSON files) or `Custom` (created in the designer). Only `Custom` ones are editable; the others are shown read-only. Extending a standard DocType will be done through Customize Form (L3), never by editing it.
- Custom definitions are stored as the same JSON in the `__doctype` table, so they can be exported to files later and shipped as an app.
- The `MetaRegistry` is now mutable but **snapshot based**: readers see an immutable dictionary and a change swaps it atomically, so a running request never sees a half-updated registry.
- Saving a definition: validate (every problem reported with a path such as `fields[2].name`) → check references and the supported feature set → preview the schema → apply DDL in one transaction → store the definition (optimistic concurrency on `modified`) → swap the registry. The new DocType works immediately.
- Schema safety is the same as for migrations: additive only. Specifically the designer:
  - never drops a column; a removed field keeps its column and data (the preview says so);
  - refuses to change the type of a saved field, and refuses names that are reserved or already in use;
  - turns uniqueness on and off by creating and dropping the index only; if existing rows hold duplicates, creating the index fails, the transaction rolls back and the user gets a plain message;
  - deletes a DocType (and drops its table) only when it has no documents and no other DocType links to it.
- Only Administrators and System Managers may use the designer; the server enforces it.
- The Desk offers only what the engine supports today. The field type catalog comes from the server (`/api/designer/field-types`), with unsupported types (Table, Attach, Password, ...) shown disabled, so the UI never promises more than the engine can do.
- Field `description`, `hidden` and `readOnly` are UI hints, like in Frappe; they are not enforced by the API.

## Consequences
- In a multi-instance deployment other instances learn about a change only on restart (or a future cache invalidation message). Single-instance deployments are fully consistent.
- A DocType cannot be renamed yet, and field names and types are fixed once saved. Both need data-migration support and are planned with Customize Form.
- Table names are `tab` + DocType name, so `Series` is reserved, as are the names of planned framework DocTypes (Comment, ToDo, Workflow, ...).
