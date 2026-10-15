# ADR-024: Dashboards, number cards, charts and lists

**Status:** accepted (L9, part 1)

## Context
People want to see their business at a glance: how many open orders, sales by month, the latest tickets. Frappe splits this into Number Cards, Dashboard Charts, Dashboards and Report Builder. They are one idea: small queries over documents, shown as tiles. The framework already had a permission-safe grouped report (ADR-010); dashboards build on the same engine.

## Decision
- **A dashboard is a row of the Core DocType `Dashboard`** (title, `is_public`, and its tiles as JSON), owned by whoever created it, and private to them or shared with everyone signed in. The owner (or a System Manager) edits and deletes it; people have at most 50 and a dashboard at most 24 tiles. A private dashboard of someone else answers "not found".
- **Three tiles.** *Number*: count, or sum, average, smallest or largest of a numeric field. *Chart*: the same aggregate per value of a field, as bars, a line or a pie; a Date or Datetime field can be grouped by day, month or year and is then in date order (the average per month is the average over that month's documents, not of averages); other fields show the biggest groups first, at most 30 points. *List*: up to 8 chosen fields of up to 50 documents, ordered by a field. Every tile can have up to 10 filters (field, operator, value, the same as the list's).
- **Always the viewer's permissions.** A tile is evaluated for whoever is looking, through the normal list rules (`DocumentService.AggregateAsync`/`SeriesAsync` read the rows the person may read, so own-only roles, shares, User Permissions and blocked modules all apply) and needs the **report right** for numbers and charts (read is enough for lists). A shared dashboard therefore never shows anyone more than they could see themselves; a tile they cannot see shows "You do not have access to this" while the others still load. Password fields cannot be grouped, summed, listed or filtered.
- **Validated when saved**: DocTypes and fields must exist, functions, chart types, periods and colours come from fixed lists, numeric fields are required for sums, filter operators and values are length-limited (objects refused); problems are keyed by tile and field (`items[2].groupBy`). Tile ids are made by the server. A **preview** endpoint runs one unsaved tile for the builder.
- **Desk.** A Dashboards page (list of dashboards; a view with the tile grid, refresh, edit and delete; a builder dialog with per-tile settings, conditions and a live preview). Charts are small hand-written SVG components (no chart library): axes with round tops, tooltips, a legend for pies, and a hidden table of the same numbers for screen readers. Colours are design tokens, adjusted for dark mode.

## Security
Dashboards add no new way to read data: every number comes out of `ListAsync` with the viewer's rights, and the report right is required where the list would not show an aggregate. Filters go through the list's operator and column whitelist (parameters only; unknown fields and operators are refused or shown as a tile error). Titles are text, colours and chart types are fixed classes, and the JSON stored is re-read through the same checks. Tests cover tiles for users without report rights, own-only roles, outsiders, password fields, hostile field names, private and public dashboards, non-owners and stale saves.

## Consequences
- Not yet: auto-refresh, tile positions and sizes (tiles flow in order), cross-DocType joins, report views with many columns and grouping (ADR-010's report and CSV export remain), saving a dashboard as a workspace page, scheduled email of a dashboard, and per-user default dashboard.
- The next L9 parts are Workspaces (the home page), print formats and letter heads.
