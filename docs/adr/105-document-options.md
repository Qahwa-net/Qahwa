# ADR-105: Document options: default sort, copy and file limit

Status: accepted

## Decision
Three options on a DocType's definition (set in the designer's DocType page):
- `sortField` / `sortDescending`: how the list is sorted until the person chooses otherwise ("name", "modified", "creation" or one of the DocType's own fields; a person's saved list settings still win). Checked when the DocType is defined, so a value can never name something that is not a column.
- `allowCopy` (default true): false hides **Duplicate**. Display only: a person who may create documents can still create one with the same values by hand.
- `maxAttachments` (1 to 50): the most files one document may have; the system limit of 50 still applies. Enforced on the server when a file is uploaded.

A child table cannot set a sort or a file limit (it has neither a list nor files).
