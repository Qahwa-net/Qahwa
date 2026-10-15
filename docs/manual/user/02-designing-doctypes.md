# Designing your own DocTypes

**Tools → DocType Designer** (System Managers). You can create new kinds of documents without code. DocTypes that ship with Qahwa or an app are read-only here; to extend those, use [Customize Form](03-customize-form.md).

## Create one

1. **+ New DocType**. Give it a name (for example *Supplier*) and, optionally, a module.
2. Choose its kind (cannot be changed later): normal, a **child table** (rows inside another DocType), **one document only** (settings), a **tree**, and whether documents are **submitted and then locked**. Tick **can be renamed** if documents may be renamed or merged.
3. **Add fields**: pick a type (text, number, date, choice, link to another DocType, table ...), give it a label. The name (used in the database) follows the label.
4. Set how documents are **named**: random ID, a numbered series (*SUP-00001*), from a field, or typed by the user. Pick a **title field** so lists and links show a friendly name.
5. Add **permissions**: which roles can read, write, create, delete, export, report (and submit/cancel).
6. Save. The preview shows exactly what changes in the database. The DocType works immediately: it appears in the sidebar for users with access.

## Field options

Required, unique, show in list, default value, help text, hidden, read only, and the **permission level** (above 0 only roles with a rule at that level can see it). Under **Logic**:

- **Show only when**, **Required when**, **Read-only when**: conditions such as `status == 'Closed'`.
- **Calculate as**: a formula such as `qty * rate`; the field fills itself.
- **Fetch from**: copy a value from a linked document (the customer's territory).
- **Rules** on the DocType: "the document must satisfy this before it can be saved", with a message.

The expression help (Fields and functions) lists what you can use; see also [the expression language](../../expressions.md).

## Child tables

Create a DocType marked as a child table with the columns you need, then add a **Table** field in the parent DocType pointing at it.

## Limits

The type of a saved field cannot change; a DocType that has documents cannot be deleted; DocTypes cannot be renamed yet. Two people editing at once: the second save is refused with a message so nothing is overwritten.
