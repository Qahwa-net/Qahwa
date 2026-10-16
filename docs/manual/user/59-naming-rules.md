# Naming documents by rule

## From an expression
In the DocType designer, **Document names → From an expression** lets you build a name from the document's fields, for example `upper(region) + "-" + customer` gives `NORTH-Acme`. The name must be unique, so use something that is. Names cannot contain line breaks and are at most 140 characters.

## Different names for different documents
A System Manager can open **Document Naming Rule** and add rules for a kind of document:

| Field | Meaning |
|---|---|
| DocType | The kind of document the rule is for |
| Priority | The lowest number is tried first |
| Condition | Optional. An expression such as `amount >= 1000` or `kind == "company"`. Empty means always |
| Naming rule | A series such as `BIG-{YYYY}-.#####`, `field:customer` or `expression:…` |
| Enabled | Switch a rule off without deleting it |

When a document is saved for the first time, the first enabled rule whose condition is true names it. If none matches, the DocType's own naming applies. Each series prefix has its own counter (see **Naming Series**).
