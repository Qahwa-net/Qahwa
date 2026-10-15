# ADR-011: Single and tree DocTypes, naming series manager, rename and merge

**Status:** accepted (L6)

## Decision
- **Single DocTypes** (`isSingle`) have one document named after the DocType, for settings. It has no list: the Desk opens the form directly. Until it is first saved, reading it shows the field defaults; the first save creates it. It cannot be deleted through the API (deleting the DocType in the designer removes it). Singles cannot be submittable, renamable, trees or child tables, and have no naming rule.
- **Tree DocTypes** (`isTree`) get a `parent_node` Link (to the DocType itself) and an `is_group` Check, added automatically. The engine enforces that a parent is a group, a node never becomes its own ancestor (bounded walk), a group with children stays a group, and a node with children cannot be deleted. `GET /api/tree/{doctype}?parent=` serves one level through the normal list rules, and the Desk has an expandable tree view that loads a level at a time.
- **Single and tree are fixed at creation**, as is `isChildTable`, because changing them would change the meaning of existing rows.
- **Naming series manager.** Lists every prefix used by a DocType's `PREFIX-.#####` rule (and any stored counter), with its digits, counter and users, and lets a System Manager set the counter. Only known prefixes can be set, and the number is limited, so the series table cannot be filled with arbitrary names. Setting it below numbers already used makes new documents clash; the page says so.
- **Rename and merge** (`allowRename`, off by default). Needs write access through roles (a share is not enough); a merge also needs delete access, and write on the target. A submitted document cannot be renamed. One database transaction updates the document's name, its child rows, every Link field that points at it (also in child tables), the shares and the User Permissions naming it; a merge repoints everything, then removes the old document and its rows. Identifiers always come from the DocType definitions (whitelisted), values are parameters, names are length-limited and cannot contain control characters. Merging a tree node into its own descendant is refused.

## Consequences
- A link stored as plain text (a Data field holding another document's name) is not updated; only Link fields, shares and user permissions are.
- Tree move and reorder by drag and drop, nested-set queries ("all descendants") and per-tree permissions are not done yet.
