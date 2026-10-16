# ADR-078: Table multi-select

Status: accepted

## Context
Choosing several documents of one kind (the skills of a candidate, the tags of a product) was done with a grid of rows holding a single Link field each, which is clumsy to fill in. Frappe calls the nicer version "Table MultiSelect".

## Decision
- **A flag, not a new field type.** A Table field can be marked `multiSelect`. Data are stored exactly as for any table (rows of a child DocType), so every existing feature (permissions, history, reports, export, import, API) keeps working. Only the Desk draws it differently: a list of chips with a search box, instead of a grid.
- **The rule for the child DocType:** it must have exactly one Link field, and no other field may be required (a chip could not fill it in). Checked when DocTypes are loaded (`DocTypeMetaExtensions.MultiSelectLink`, `MetaRegistry.CheckReferences`); only Table fields may be multi-select.
- **Each document at most once:** saving a multi-select table with the same value twice is refused ("This value is chosen twice", on the second row). Plain tables are unchanged and may repeat values.
- **Desk:** the DocType designer offers "Show as a list of chips" on Table fields; the form shows a `ChipPicker`. Adding a chip adds a row; removing one removes its row.

## Consequences
- The saved data are still rows with ids and an order; chips show them in that order.
- Any extra (optional) fields of the child are kept for rows made another way (the API) but cannot be edited from the chip view.
