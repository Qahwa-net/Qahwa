# Field rules (for DocType designers)

In the DocType designer, open a field to set:
- **Set only once**: people can fill it in, but after that it can no longer be changed (for example a serial number).
- **Not below zero**: number fields refuse negative values.
- **Longest text**: text fields refuse text longer than the number you enter (1 to 10000).

- **Do not copy when duplicating**: the field starts empty in a duplicate, an amended or a repeated document.
- **Bold label** and **Offer as a list filter** (it appears in the list's filter sidebar).

The rules apply everywhere: forms, imports, bulk edits and the API.
