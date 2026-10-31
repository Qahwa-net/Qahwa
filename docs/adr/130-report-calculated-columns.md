# ADR-130: Calculated columns in list reports

**Status:** accepted (L9, follows ADR-045)

## Context
Reports could only show fields as they are stored. "Amount times quantity", "days between start and end" or "Large or Small" meant adding a calculated field to the DocType for every report anyone wanted.

## Decision
- A list report can have up to five **calculated columns**: a name and a formula in the same safe expression language as formulas and rules (`amount * qty`, `daysBetween(start, end)`, `amount >= 100 ? 'Large' : 'Small'`). They are worked out for each row and shown after the chosen columns, in the preview, the report page and the CSV or Excel download.
- **Only the document's own fields.** A formula may use fields of the DocType (not passwords, not `parent`, not `lookup`). It is checked when the report is saved: it must parse and every name must be a field a report can read, the name of the column must not repeat another, and summaries cannot have calculated columns.
- **The viewer's rights decide.** The report is run for the person looking, through the ordinary list rules, so the fields a formula needs are fetched like any column: if the viewer may not read one (a higher permission level), the report is refused and names that field, exactly as asking for it as a column would be. A formula therefore can never reveal a field a column could not. Fields only a formula needs are not shown.
- **Values.** A column whose results are all numbers is a number column (sorted and right-aligned like one); anything else is text. Results that are not finite numbers (division by zero gives no value) show empty.

## Consequences
- Not yet: calculated columns in summaries (totals of a formula), sorting or filtering by a calculated column, formulas across rows or other documents, and joins.
