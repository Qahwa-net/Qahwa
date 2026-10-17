# ADR-089: Personal Desk and role navigation defaults

Status: accepted

## Context
Workspaces provide shared home-page content, and list settings remember individual list choices. The sidebar was still built directly from metadata, with no persistent personal ordering, favorites or visibility.

## Decision
- Add `Desk Setting`, a Core DocType written through `DeskSettingsService`. A deterministic SHA-256 key of scope plus target identifies one personal or role layout. There are no ordinary document permissions on its rows; each personal API uses only the authenticated caller, and role APIs require System Manager or Administrator.
- Persist identifier lists for favorites, hidden links, ordering, group ordering/collapse, Home shortcuts and a default workspace. No HTML, arbitrary URLs or client-supplied paths are stored. The server constructs link destinations from permitted metadata, known Desk routes, accessible dashboards/reports and the person's saved filters.
- Resolve defaults in this order: standard Desk, the first configured role in ordinal role-name order, then the user's full personal layout if present. A personal layout replaces the selected role's layout. Reset deletes only that user's override. Multiple role defaults are deliberately not merged, to avoid contradictory visibility and ordering choices.
- Validate writes and prune reads against current permissions and available workspaces. A stale setting cannot grant access. Deleting an item or losing permission removes its effective shortcut without rewriting the stored layout.
- Use optimistic concurrency on writes. Links not listed in an ordering retain their normal order, so newly available DocTypes still appear. The Customize Desk control remains outside customizable navigation, allowing recovery when every menu item is hidden.
- React Query shares per-user settings between Shell and Home. The editor previews the sidebar before saving, supports drag ordering and keyboard move buttons, and offers role defaults to managers. Home workspace selection and personal shortcut ordering do not modify a shared workspace definition.

## API
- `GET/PUT/DELETE /api/desk-settings`: effective preferences, save personal layout (`{ layout, modified }`), reset personal layout.
- `GET/PUT/DELETE /api/desk-settings/roles/{role}`: inspect, save and remove role defaults; managers only.

## Consequences
Preferences follow the user across devices. Saving a personal layout freezes that layout until the user resets or changes it; later administrator defaults do not overwrite it. Home card/section layout continues to use the existing Workspace and Dashboard builders. Private per-user widget arrangements and workspace cloning are future work.
