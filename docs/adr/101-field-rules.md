# ADR-101: Field rules: set once, non-negative, longest text

Status: accepted

## Decision
Three field properties are enforced on the server, for every way of saving (Desk, API, import, bulk edit):
- `setOnce`: the first value can be given at any time, but a field that already has a value cannot be changed or emptied.
- `nonNegative`: Int, Float, Decimal, Currency and Percent fields refuse values below zero.
- `maxLength` (1 to 10000): text fields (not Password) refuse longer text with a clear message.

Defining a DocType with a meaningless combination (non-negative on text, a length on a number, set-once on a layout or table field) is refused. The designer shows the three settings on each field. Three more properties shape the forms and lists: `noCopy` (not copied by Duplicate, Amend or Auto Repeat), `bold` (the label is shown in bold) and `inStandardFilter` (offered in the list filter sidebar; only Select, Link, Check, Data, Email, Phone, Int and Rating). `precision` (0 to 9) rounds Float, Decimal, Currency and Percent values to that many decimals when they are saved (halves away from zero), and the Desk shows exactly that many. Other cosmetic properties (in preview, translatable) stay unbuilt; they would not change what data is accepted.
