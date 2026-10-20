# ADR-102: Heading and HTML fields

Status: accepted

## Decision
Two layout field types put fixed text on a form without holding a value:
- **Heading**: a line of bold text (its label).
- **HTML**: a block of formatted text kept in the field's `options`. It is cleaned with the framework's allow-list sanitizer **when the DocType is defined** (so a stored definition never holds scripts, event handlers or `javascript:` links) and cleaned again by the Desk before it is drawn.

Both are layout types: no column, no value (sending one is refused), skipped by duplicate, auto repeat, web forms and client scripts. A **Button** field is not added: buttons on a form are made with Document Actions (ADR-023 area) and client scripts (ADR-077), which can also run code, which a plain field could not do safely.
