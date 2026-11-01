# ADR-138: Renaming and retyping saved fields

**Status:** accepted (platform)

## Context
Until now the designer refused to rename a saved field or change its type, because the schema planner only adds columns. People who chose the wrong type or name had to add a new field and copy the data by hand.

## Decision
- **A rename is explicit.** A field in the designer's definition may carry `renamedFrom` (the name it had). It is never inferred from a changed name, because a missing field plus a new field could equally be a removal and an addition. The designer checks that the source is a saved field with a column, that the new name is not taken, and that it is not renamed twice.
- **The column is renamed, not copied.** `ALTER TABLE … RENAME COLUMN` (PostgreSQL) or `sp_rename` (SQL Server) keeps every value. The indexes (their names are made from the column name) are dropped and made again under the new name. Settings of the DocType that name the field (title, status, sort, image, search fields, rule fields) follow. `renamedFrom` is removed before the definition is stored, so saving again does not ask for it again.
- **A type change is limited to conversions that keep every value** (`FieldTypeChanges`): between text types; a number to text or a wider number; text to a number, date or time; Date to Datetime; Check to or from Int. A Decimal to an Int, a Datetime to a Date, Password, Table and layout types are refused with the reason, on the field.
- **The conversion is one transaction.** The planner drops the column's indexes, runs `ALTER COLUMN … TYPE … USING` (PostgreSQL) or `ALTER COLUMN` (SQL Server), and makes the indexes again. If one saved value does not fit (text in a field that becomes a number, text too long for a short field) the database refuses, the transaction rolls back, and the designer answers with a conflict that says nothing was changed.
- **The planner still never changes a column by itself.** A column that differs from its DocType only gets converted when the designer asks for it (`SchemaChanges.Retypes`); otherwise the old warning and block stay, so a database changed behind our back is still noticed.
- The Desk lets a saved field's name be edited and shows a Type list for it; the preview lists the rename or change and the notes.

## Consequences
- References outside the DocType (conditions and formulas of other fields, other DocTypes' fetch rules, saved reports and filters, workflows, print formats, webhooks that name the field) are not rewritten. The preview warns about mentions inside the same DocType only.
- The in-memory test schema manager records the changes it was given but does not move data; the real conversion is covered by PostgreSQL tests (rename keeping values and the unique index, a conversion across Int/Decimal/Data, and a refused conversion that changes nothing). SQL Server SQL is written but not run against a server here.
- Not yet: renaming a DocType, rewriting references outside the DocType, standard (non-designer) DocTypes.
