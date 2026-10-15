# ADR-026: Workspaces

**Status:** accepted (L9, part 3)

## Context
The home page should be the place each person starts: links to the things they use, and the numbers they care about, arranged by an administrator for their role. Until now it listed every DocType.

## Decision
- **A workspace is a row of the Core DocType `Workspace`** (title, position, roles, and its blocks as JSON), written only by `WorkspaceService` and managed by System Managers (up to 30 workspaces). **Roles** say who sees it (none means everyone signed in; Administrators see all).
- **Blocks**: a *heading*, a row of *shortcuts* (up to 24) or a *dashboard* (its tiles shown in place). A shortcut leads to the documents of a DocType, to a dashboard, or to one of the Desk's own pages from a fixed whitelist (`WorkspaceService.Pages`); there are **no free URLs**, so a workspace cannot send anyone to another site or run a `javascript:` link.
- **Filtered for each person.** `MineAsync` returns the workspaces for the caller's roles in position order, with each shortcut kept only if the caller may use it: a DocType they can read, a dashboard they can open, a tool page only for System Managers. A block left with nothing disappears, and a dashboard block only appears if the dashboard is visible to them; its tiles are then worked out with their own permissions (ADR-024). So a workspace never reveals that a DocType, dashboard or tool exists to someone who cannot use it.
- **Desk.** The home page shows the person's workspaces as tabs (a single one as a title) above the DocType tiles; a Workspaces page (Tools) lets a System Manager add, order and delete blocks, pick DocTypes, dashboards and pages from lists and roles from a search box, with errors under the block they belong to. Headings are rendered as text.

## Security
Only System Managers manage workspaces. Everything is validated on save (block types, kinds, that DocTypes, dashboards and roles exist, that pages are on the whitelist, lengths and counts), and everything shown is filtered again on every read with the viewer's rights. Tests cover role visibility, shortcuts to unreadable DocTypes and tool pages, private dashboards, hostile targets (`../../etc`, `https://evil`, `javascript:`), heading text and limits.

## Consequences
- Not yet: per-user layouts and favourites, shortcut counts and colours, icons, a "my default workspace", route-level workspaces (a page per workspace under /app), and charts embedded singly (a dashboard block shows the whole dashboard).
- This completes the planned L9 presentation features apart from multi-column report views.
