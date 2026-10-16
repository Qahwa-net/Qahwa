# ADR-061: The search box (global search and commands)

**Status:** accepted (L9)

## Context
People need to jump to a page, open or create a DocType, or find a document by what it is called, without clicking through the sidebar. The search must never show something a person may not see, and the text typed must never be able to widen a query.

## Decision
- **`GET /api/search?q=`** (signed-in only) returns the DocTypes whose name contains the text (with whether the person may create one) and up to 30 documents.
- **Documents are found through the same reads as a list** (`DocumentService.ListAsync`), so permissions, own-only rules, shares, User Permissions, blocked modules and field-level permissions all apply; a DocType whose title field the person may not read, or that they may not open, yields nothing instead of an error. Documents are searched by the title field (or by name when there is none), and also by name when the text looks like a name (contains a digit or a dash). System DocTypes (module Core) and single DocTypes are not searched.
- **The text is a literal.** `\`, `%`, `_` and `[` are escaped, so `%` finds a percent sign and not everything (this works the same on PostgreSQL and SQL Server, see ADR-060); control characters and more than 100 characters are refused; every value is a bound parameter.
- **Limits:** at most 60 DocTypes asked, 5 results per DocType (newest first), 30 in all, and 120 searches per minute per person.
- **Desk:** a search button and Ctrl+K (⌘K) open a dialog with an accessible combobox. What is known in the page (pages, DocTypes from the menu, "new …" / "open …" commands, ranked by start-of-word match, accent- and case-insensitive) shows at once; the server is asked 200 ms after typing stops (two letters or more) and adds "New X" entries and documents. Addresses are built with encoded parts only. Tools appear only for the people who may use them.

## Consequences
- Searching by other fields (a description, a phone number) is not done; neither is ranking by relevance or a full-text index. A tagged "show in search" field list per DocType could be added later.
- A document that a person may open only through a share is found; one hidden by a User Permission is not.
