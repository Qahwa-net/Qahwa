# ADR-105: Document options: default sort, copy and file limit

Status: accepted

## Decision
Three options on a DocType's definition (set in the designer's DocType page):
- `sortField` / `sortDescending`: how the list is sorted until the person chooses otherwise ("name", "modified", "creation" or one of the DocType's own fields; a person's saved list settings still win). Checked when the DocType is defined, so a value can never name something that is not a column.
- `allowCopy` (default true): false hides **Duplicate**. Display only: a person who may create documents can still create one with the same values by hand.
- `maxAttachments` (1 to 50): the most files one document may have; the system limit of 50 still applies. Enforced on the server when a file is uploaded.

A child table cannot set a sort or a file limit (it has neither a list nor files).

- `defaultView` (list, tree, board, calendar, gallery, map or timeline; tree needs a tree DocType and gallery an Image field): the first time a person opens the DocType in a browser session they land on that view; the list icon always returns to the list. It is a convenience stored per session in the browser, not a restriction. `imageField` names the Image field the gallery shows by default.
- `searchFields` (up to five different text fields, checked when the DocType is defined): choosing a document in a Link field also searches these fields, besides the name and title. The global search box uses the same fields. Only fields the person may read are searched (a field hidden from them by permission level cannot be used to probe for its values).
