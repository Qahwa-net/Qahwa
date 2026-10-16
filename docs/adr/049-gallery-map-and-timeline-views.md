# ADR-049: Gallery, map and timeline views

**Status:** accepted (L12, part 8)

## Context
After the board and the calendar (ADR-031), documents with pictures, places or a start and an end date also deserve their natural view.

## Decision
Three more Desk-only views over the existing list API (no server code, no new permission, the same rules as the list):

- **Gallery** (`/app/<DocType>/gallery`): one tile per document from one of its Image fields, lazily loaded through the file endpoint (so each picture is checked against the document's read permission), with the document's title; choose the field with `?by=`.
- **Map** (`/app/<DocType>/map`): the places of a Geolocation field as points on a **plain grid of latitude and longitude** fitted to the places shown (same scale on both axes, margin, one place centred), each point linking to the document, plus a table with a "View on a map" link to OpenStreetMap (numbers only in the address, `noopener noreferrer`). There is **no background map on purpose**: loading map tiles would tell a third party where and when each viewer looks at which area, and would need a looser content-security policy. Documents without a place are counted, not hidden silently.
- **Timeline** (`/app/<DocType>/timeline`, a Gantt chart): a bar from a start date to an end date for each document, an axis with 12 or fewer ticks (day, week, fortnight, month, quarter or year steps), a dashed "today" line, start and end chosen from the DocType's date fields (guessed from their names, changeable). Documents with a missing date or an end before the start are counted, not drawn.
- All three show at most the 500 most recently changed documents and say so; the toolbar buttons appear only when the DocType has the fields they need. The layout work is in pure, tested functions (`lib/gantt.ts`, `lib/map.ts`).

## Consequences
- Not yet: dragging a bar to change dates, dependencies between bars, zooming and panning the map, marker clustering, a background map (would need a tile service decision), filters shared with the list.
