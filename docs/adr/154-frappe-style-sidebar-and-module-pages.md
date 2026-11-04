# ADR-154: A sidebar that folds, workspace pages and module pages

**Status:** accepted

## Context
The sidebar listed every group fully open (the system's own `Core` DocTypes alone are 60 links), only groups saved by the person could be folded, workspaces appeared only as tabs on the home page, and the home page was a flat wall of every DocType. People coming from Frappe expect a short left-hand list of workspaces and modules, each opening a page of its own. The form's header also repeated **Assign** and **Share** although the side cards already offer them.

## Decision
- **Default order** (before the person arranges anything): *My work*, *Workspaces*, the business modules (alphabetical), *Tools*, and the system's own *Core* last. A person's saved group order still wins; groups that did not exist when they saved come after theirs.
- **Everything folds.** Each group header shows how many links it holds and a chevron; **Expand all** and **Collapse all** sit under *Customize Desk*. *Tools*, *Core* and any group of more than 12 links **start folded**, except the group that holds the page being viewed. A choice the person makes is kept: folded groups are saved with their desk (they follow them to other devices), and groups unfolded among those that start folded are remembered on the device. Folding a group that is only folded by default saves nothing.
- **Workspaces are sidebar entries.** The workspaces made for the person (Workspaces page, ADR-143) are listed as a *Workspaces* group; each opens `/workspace/<id>` with its heading and blocks. The server adds them to the desk links, so they can be hidden, pinned, reordered and folded like any other entry.
- **Module pages.** Each business module's header has a small link to `/module/<module>`: a page with the module's description and a card per DocType, with a breadcrumb back to Home. The home page now shows one card per module (with its description or DocType count) instead of every DocType.
- **Drag to arrange.** A group's title can be dragged before another group, and a link before another link of the same group; the new order is saved with the person's desk (`groupOrder`, `order`, or `pinned` for Favorites; a dragged group writes the whole visible order so nothing shifts later). Customize Desk offers the same with buttons, which is the keyboard route. A link cannot be dragged into another group.
- **The form header no longer has Assign and Share icons.** The *Assigned to* and *Shared with* cards beside the document (with their plus buttons) are the one place for both.

## Consequences
- Verified in the running app: the folded defaults, Collapse all / Expand all (saved and kept across a reload), a workspace opened from the sidebar, a module page, and the form header without the two icons. Unit tests cover the order, the folding rules and a saved choice winning; an API test covers workspaces in the desk links and folding the group.
- A person who saved a layout before this change keeps their order and their folded groups; they can press **Expand all** or **Collapse all** once to start over.
- Dragging was checked in the running app by dispatching the browser's drag events (a group moved, a link moved, both kept after a reload); it was not tried with a real mouse or on a touch screen, where browsers do not start drags from long-press the same way.
- Not yet: nested entries inside a workspace (Frappe's child pages), icons per workspace, a workspace page for *Tools*.
