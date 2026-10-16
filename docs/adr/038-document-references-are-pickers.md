# ADR-038: References to DocTypes and documents are pickers, never free text

**Status:** accepted

## Context
Fields such as "DocType" and "Document" on Auto Repeat, Document Action, Web Form, Print Format, Email Template, ToDo, Comment and others were plain text. People mistyped names, nothing told them which DocTypes exist, and a wrong name only failed later.

## Decision
- **A virtual link target `DocType`.** A `Link` field with `options: "DocType"` holds the name of a DocType. The picker offers the DocTypes the person may read (with their module as a hint); titles are given only for readable ones. On save the value must be a real top-level DocType (existence only, so rows made on someone's behalf, such as a comment on a shared document, are not refused). The registry accepts the target although there is no DocType called "DocType"; designers can also use it in their own DocTypes.
- **`DynamicLink` is real.** Its `options` names the sibling field that holds the DocType. The Desk shows a document picker for that DocType (disabled with "Choose the DocType first" until one is chosen), in forms and in table rows, and clears the document when the DocType changes. No existence check on save: documents get deleted while history that refers to them (webhook deliveries, comments, versions) must stay writable.
- **Applied to every Core reference:** `reference_doctype` and `doc_type` and `target_doctype` are DocType links; `reference_name` is a Dynamic Link on `reference_doctype` (Auto Repeat, Comment, Email Queue, File, ToDo, Webhook Delivery). The column type in the database is unchanged.
- **Rule for new work:** a reference to a DocType or a document is a Link or Dynamic Link field, never a Data field.

## Consequences
- Typos are impossible in the Desk and refused by the API.
- Not yet: choosing the DocType's *field names* (watch field, target field, web form fields) from a list; those are still text.
