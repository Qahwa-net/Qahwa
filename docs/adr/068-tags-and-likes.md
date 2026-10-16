# ADR-068: Tags and likes

Status: accepted

## Context
People want to mark documents ("urgent", "q3 review") and to like them, and to find those documents again in a list. Frappe has both; the plan listed them as missing.

## Decision
- **Two small tables:** `Document Tag` (`ref_doctype`, `ref_docname`, `tag`) and `Document Like` (`ref_doctype`, `ref_docname`, `user`), both framework DocTypes (System Manager can read them; everything else goes through `LabelService`).
- **Tags:** trimmed, lower case, 1 to 40 characters, no commas or control characters, at most 20 per document, added once. Adding or removing needs the right to **change** the document (and reading it to see them). `GET/POST /api/resource/{doctype}/{name}/tags`, `DELETE .../tags/{tag}`, `GET /api/tags/{doctype}` (the tags in use, most used first).
- **Likes:** one per person per document; anyone who can read the document may like it. `GET/POST/DELETE /api/resource/{doctype}/{name}/like`.
- **Only for the documents people work with** (not child rows, single DocTypes or the framework's own DocTypes), the same rule as assignment rules.
- **Popular tags show only what the person may see:** they are counted over the documents the person can list (their 1,000 most recently changed), so a tag on a private document is never revealed.
- **List filters:** `["_tag", "=", "urgent"]` and `["_liked_by", "=", "__user"]` (or an email) work in any list, count, group-by and export. They become a database filter `name IN (SELECT ref_docname FROM <tag table> WHERE ref_doctype = ? AND tag = ?)` (new `FilterOp.Marked`); the table is chosen by the framework, the values are parameters, and only `=` is accepted.
- Tags and likes are removed when the document is deleted and follow it when it is renamed or merged.
- Desk: a Tags card in the form sidebar (chips, add with suggestions, remove), a Like button with its count next to Follow, and "Liked by me" and the popular tags as filter chips above the group-by counts.

## Consequences
- Tags are not part of a document's saved data, history or exports; a restored deleted document comes back without its tags and likes.
- Saved filters cannot hold `_tag` or `_liked_by` yet (saved filters are checked against real fields).
- Likes do not notify anyone.
