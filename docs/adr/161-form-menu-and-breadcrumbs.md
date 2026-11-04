# ADR-161: A Menu in the form header, and breadcrumbs

**Status:** accepted

## Context
A document's header held up to ten icon buttons in a row (like, follow, duplicate, print, email, remind, repeat, rename, delete, then submit or cancel and Save), each needing a hover to be understood, with the delete icon sitting beside the print icon. Frappe's form puts the everyday actions behind a **Menu** and keeps the header for the title, the status and the one primary action, and shows where the document is with a breadcrumb.

## Decision
- **A Menu dropdown** (`ActionMenu`) holds the secondary actions, each with its name and icon: *Duplicate, Print, Email, Remind me, Repeat, Rename, Delete* (Delete in red, last). Each appears under exactly the conditions its icon did (permissions, document state, DocType settings); nothing is drawn when none apply.
- **The header keeps** the Like and Follow icons, the workflow bar, Submit / Cancel document / Restore, the DocType's own buttons and **Save**: the actions that change the document's state stay one click away.
- **Keyboard and screen readers:** the button announces a menu and whether it is open; the arrow keys, Home and End move between the actions (disabled ones are skipped, the list wraps), Escape closes it and returns focus to the button, and clicking elsewhere closes it.
- **Breadcrumb** above the title: *Home › Module › DocType*, each part a link (the module goes to its page, ADR-154; a settings page shows its own name last).

## Consequences
- Tested: the menu draws nothing without actions, opens, runs and closes, moves with the keys and wraps, closes on Escape with focus returned and on an outside click. Seen in the running app: the breadcrumb and a menu of Duplicate, Print, Email, Remind me, Repeat and Delete beside Save.
- **The list header follows the same pattern:** a breadcrumb (*Home › Module*), the view switches (group by, columns, tree, board, calendar and so on) as before, a **Menu** with *Report*, *Import* and the exports (CSV, CSV with table rows, Excel; the exports are download links), and a primary **New {DocType}** button in place of the plus icon. The menu items appear under the same conditions as the icons they replace. Seen in the running app on the Customer list.
- Not yet: moving Like and Follow into the page's sidebar, and a menu for the list's own view switches.
