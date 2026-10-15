# ADR-013: Customize Form (custom fields and property changes)

**Status:** accepted (L3)

## Context
DocTypes that ship with the framework (User, Role, ...) or an app cannot be edited in the DocType Designer, yet every installation needs "one more field on User" or "call this field something else". Editing the shipped definition would be lost on upgrade and cannot be undone.

## Decision
- **A customisation is data, kept beside the definition.** One row per DocType in `__customize` (JSON): custom fields, per-field property overrides, the field order, and the title field. The registry holds the *effective* DocType (definition plus customisation); the definition is remembered, so a customisation is always applied to the original and **Reset** restores it exactly.
- **What can change.** New fields (any supported type except Table and Password, named `custom_...` so they cannot clash with the framework's own later fields). On existing fields: label, help text, hidden, read-only, required, default, show in list, permission level, the choices of a Select, and the three condition expressions. Order and title field. Not: a field's type, its link target, uniqueness or naming, which would change storage.
- **Schema.** A custom field gets a real column through the normal additive schema sync, before the customisation is stored. Nothing is ever dropped: removing a custom field or resetting hides the field and keeps its data, and adding it back brings the data back.
- **Validated like any definition.** The effective DocType goes through the same checks as a designed one (names, types, expressions, link targets, permission levels); errors come back keyed by field name (`fields[custom_phone].label`) so the page can mark the right field. Nothing is applied if anything is wrong.
- **Who and where.** System Managers customise App DocTypes; only an Administrator touches Core ones. DocTypes made in the designer are edited there, not here (that would give two sources of truth), and child tables are not customised yet. Concurrent edits are detected with the stored modified time.
- **Start-up.** Customisations are applied right after the designer's DocTypes, before the schema sync, so the columns exist; a broken one is reported and skipped, the rest apply.
- **Desk.** `/customize`: a DocType list, then one editor with every field (expand for properties, arrows to reorder, a type picker to add), a title-field choice, Save and Reset. Forms, lists, links and permissions use the new fields at once.

## Consequences
- Not yet: customising child tables, custom fields inside table rows, changing naming or the DocType's own flags, per-DocType list settings (L7), import/export of customisations as app fixtures (L12).
- A required custom field makes old documents fail validation on their next save until it is filled, as in Frappe.
