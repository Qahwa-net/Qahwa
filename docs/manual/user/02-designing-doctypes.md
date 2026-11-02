# Designing your own DocTypes

**Tools → DocType Designer** (System Managers). You can create new kinds of documents without code. DocTypes that ship with Qahwa or an app are read-only here; to extend those, use [Customize Form](03-customize-form.md).

## Create one

1. **+ New DocType**. Give it a name (for example *Supplier*) and, optionally, a module.
2. Choose its kind (cannot be changed later): normal, a **child table** (rows inside another DocType), **one document only** (settings), a **tree**, and whether documents are **submitted and then locked**. Tick **can be renamed** if documents may be renamed or merged.
3. **Add fields**: pick a type (text, number, date, choice, link to another DocType, table ...), give it a label. The name (used in the database) follows the label.
4. Set how documents are **named**: random ID, a numbered series (*SUP-00001*), from a field, or typed by the user. Pick a **title field** so lists and links show a friendly name.
   - A series prefix may contain `{YYYY}`, `{YY}`, `{MM}`, `{DD}` (today) and `{field_name}`. `INV-{YYYY}-` with 4 digits gives *INV-2026-0001*, and the numbering starts again at *INV-2027-0001* next year; `{branch}-` numbers each branch separately. A field used this way must be filled in, and only its letters and digits are used.
5. Add **permissions**: which roles can read, write, create, delete, export, report (and submit/cancel).
6. Save. The preview shows exactly what changes in the database. The DocType works immediately: it appears in the sidebar for users with access.

## Field options

Required, unique, show in list, default value, help text, hidden, read only, and the **permission level** (above 0 only roles with a rule at that level can see it). Under **Logic**:

- **Show only when**, **Required when**, **Read-only when**: conditions such as `status == 'Closed'`.
- **Calculate as**: a formula such as `qty * rate`; the field fills itself.
- **Fetch from**: copy a value from a linked document (the customer's territory).
- **Rules** on the DocType: "the document must satisfy this before it can be saved", with a message.

The expression help (Fields and functions) lists what you can use; see also [the expression language](../../expressions.md).

You can also pick a **status field** (a choice, text or link field) and give each of its values a colour: lists then show the status as a coloured label.

## Child tables

Create a DocType marked as a child table with the columns you need, then add a **Table** field in the parent DocType pointing at it.

### A table inside a table
A child table can hold a table of its own: an assembly has lines, and each line has parts. Create the inner child table first (the parts), then add a **Table** field to the line's DocType pointing at it, then the assembly's Table field pointing at the line's DocType. On the form, open a line's details (the pencil) and its parts table is there, with its own Add row, CSV and reorder buttons.

- It goes **one level deep**: a document, its rows, and the rows' own rows. The Designer only offers child tables that hold no table themselves as the inner table, and refuses a third level with a message.
- Rows inside a row follow it: removing the line removes its parts, deleting the document removes everything, and a copy, an amendment or an auto repeat carries the parts too.
- A total over the inner rows works where you write it: `sum(parts, 'qty')` in the line's formula, and `sum(lines, 'part_count')` in the document's. In a part's formula, `parent` is its line.
- A **required** inner table needs at least one row in every new line.
- The history shows a change inside as `lines › parts: row changed (Qty: 6 → 8)`. The default print shows each line's parts under it.
- Not yet: filters, reports and imports on the inner rows, and conditions ("show when") inside inner rows are only enforced when you save, not live on the form.

## Limits

A DocType that has documents cannot be deleted, and a DocType cannot be renamed yet. Two people editing at once: the second save is refused with a message so nothing is overwritten.

## Renaming a field and changing its type
Open a saved field and change its **Field name** or its **Type**. The preview on the right says what will happen before you save.

- **Rename:** the saved values move to the new name, and the title field, status field, sort field, image field, naming field and search fields follow. Conditions, formulas and rules that still use the old name are not changed for you; the preview warns when it finds one.
- **Change the type:** every saved value is converted. Allowed: between text types (Data, Text, Email, Select, Link and so on), a number to text or to a wider number (Int to Decimal), text to a number, date or time, a Date to a Datetime, and a Check to or from an Int. Not allowed: anything that would lose part of a value (a Decimal to an Int, a Datetime to a Date), and Password, Table and layout fields.
- If one saved value cannot be converted (for example "abc" in a field that becomes a number), the whole change is refused and nothing is changed. Fix those values first.

Take a backup before changing the type of a field that holds many documents.
