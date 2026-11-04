# ADR-156: Workspace icons and workspaces under workspaces

**Status:** accepted

## Context
Workspaces appeared in the sidebar (ADR-154) as a flat list of text. Frappe shows an icon per workspace and lets a workspace hold child pages, so a team's area (Sales) can group its pages (Leads, Quotes).

## Decision
- **Icon.** A workspace has an optional icon, one of the Desk's own icon names. The server accepts a plain name (letters and digits, up to 31 characters) and never anything else, so nothing can be injected through it; the Desk shows it only when it is one of its icons. The Workspaces editor offers the list.
- **Under another workspace, one level deep.** A workspace may name a parent. The server refuses a parent that does not exist, a workspace under itself, a parent that is itself under another, and a workspace that already has others under it going under one. Deleting a workspace lifts the ones under it to the top level.
- **In the sidebar** the Workspaces group lists a parent with its children indented beneath it (children whose parent is not visible to the person, for example because of its roles, appear on their own). The server sends each workspace's icon and its parent's link id with the desk links, so ordering, hiding and pinning still work per link. Dragging reorders within the group as before.
- The workspace page's breadcrumb shows the parent: *Home › Sales › Leads*.

## Consequences
- Tested: icon and parent in the views, the four refusals, children lifted on delete, and the desk links carrying icon and parent (engine and API tests); the nesting helper has Desk unit tests. Seen in the running app: a parent with an icon and a child with its own icon, indented beneath it.
- Not yet: folding a parent to hide its children, dragging a link under another, icons for the groups of modules and tools, more than one level.
