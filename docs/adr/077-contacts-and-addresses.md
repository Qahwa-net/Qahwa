# ADR-077: Contacts and addresses

Status: accepted

## Context
Most business apps need people and places, linked to the documents they belong to (a customer, a supplier, an employee). Master data such as Country and Salutation was done (ADR-046); Contact and Address were not.

## Decision
- **Three framework DocTypes in a "Contacts" module:**
  - `Contact`: salutation, first name (required), last name, a calculated full name (the title), gender, email (checked), phone, mobile, company, designation; numbered `CON-.#####`.
  - `Address`: title, type (Billing, Shipping, Office, Personal, Other), two address lines (the first is required), city (required), state, postal code, country (Link to Country), email, phone, "preferred billing" and "preferred shipping" flags; numbered `ADR-.#####`.
  - `Dynamic Link` (a child table used by both, field `links`): *the DocType* and *the document* a contact or address belongs to, so one contact can belong to several customers and a customer can have many addresses.
- **Access:** only System Managers by default (these hold personal data). Administrators give other roles access in the Role Permission Manager; an app can narrow it further with permission hooks (ADR-074). Changes are tracked (version history) and the usual features apply (tags, likes, assignments, shares, print, email).
- **Dynamic Link fields are now checked.** A field of type Dynamic Link used to accept any text. It is now validated like a Link: the field that names the DocType must be filled in, the DocType must exist (and not be a child table), and the document must exist. This protects every use of the type, not only contacts.
- **Reserved names:** `Contact`, `Address`, `Dynamic Link` and the other framework DocTypes added since ADR-014 (`Document Tag`, `Document Like`, `Email Digest`, `Auto Email Report`, `Scheduled Job Log`, `Document Naming Rule`) cannot be used for DocTypes made in the designer. A site that already made its own DocType with one of these names must rename it before upgrading, or the framework's definition clashes with it.

## Consequences
- There is no "Contact and Address" panel on a customer form yet; the links can be listed with a filter on the contact or address list.
- Renaming a linked document does not yet update the Dynamic Link rows that point at it.
