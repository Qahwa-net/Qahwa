# ADR-123: Filtering a list by a field of a child table

**Status:** accepted (L2)

## Context
A list of orders could only be filtered by the order's own fields. "Orders that contain a pen" or "orders with a line of more than 10" meant opening each one.

## Decision
- A filter field may be written `table_field.child_field`, for example `["lines.item", "=", "Pen"]` in the API and the URL. It means: the document has **at least one row** of that table where the condition holds. All the usual operators work (`=`, `like`, `in`, `>`, `between`, timespans, `__user`...), because the condition is validated and converted exactly like a filter on the document's own field, against the child DocType's columns.
- Several filters on the same table must hold on **the same row** ("item is Pen and quantity above 5" is one row, not a Pen row and a different big row). Filters on different tables, and ordinary filters, are ANDed as usual.
- In SQL it is one `name IN (SELECT parent FROM child WHERE parenttype = ? AND parentfield = ? AND ...)` with every value a parameter and every identifier taken from the child DocType's columns (new `ChildMatches` filter operator); lists, counts and the group-by sidebar all start from the same prepared query, so they agree. It runs on PostgreSQL and SQL Server through the same builder.
- Permissions: the table field must be readable by the person (permission level 0 or their level); child fields above level 0, unknown fields and Password fields are refused, so a filter can never reveal what the person may not read. The person's own row-level limits (own-only, shares, User Permissions) still apply on top.
- The Desk's "+ Filter" lists the rows' fields as "Lines › Item"; the chip shows the same label and the filter is kept in the URL like any other.

## Consequences
- Not for sorting, columns or reports' grouping: only for filtering. Saved filters keep the dotted name.
- "No row matches" (for example orders with no line for a pen) is not expressible yet; use `not in` on the document side or count rows with a rule.
