# ADR-116: Field properties "in preview" and "translatable"

Status: accepted. Replaces the "not built" limit for these two properties in ADR-107.

## Decision
- **In preview** (any value field except layout, Table, Password and Json): when a document of this DocType is chosen in a Link field, a **Preview** button next to the field opens a small card with the values of the fields marked in preview. The button does not appear when the DocType marked none. The document is read with the person's own permissions, so nothing in the card is something they could not open, and an unreadable document shows "You cannot see this document".
- **Translatable** (Data, Select, Text, Small Text, Autocomplete): the value is shown through the Translation documents of the person's language, like a label: in lists, previews and the options of a Select. Stored values and the API never change, only what is shown. A value with no translation is shown as it is.

Both are display properties; neither changes what data is accepted or who can see it.
