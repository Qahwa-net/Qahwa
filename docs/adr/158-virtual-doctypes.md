# ADR-158: Virtual DocTypes

**Status:** accepted

## Context
Some data is not ours to store: exchange rates kept by finance, a price list maintained by another system, codes published as a file. People still want to see it in lists, filter it, link to it, export it and report on it like any other DocType, without copying it into the database and keeping the copy fresh.

## Decision
- **`"isVirtual": true` in a DocType's definition.** It gets no table (the schema planner returns an empty plan, so migrations and the Designer's preview never create one) and is **read-only**: it cannot be a child table, single, a tree, submittable or renamable, and cannot hold Table, Password or file fields.
- **A source supplies the documents.** `IVirtualSource` (`DocType`, `ListAsync(query)`, `CountAsync(query)`, `GetAsync(name)`) is registered in the container, one per virtual DocType. The query it receives has already been through the permission layers, field checks and type conversion that every list goes through. `VirtualQuery.Apply`/`Count` filter, sort and page rows a source already holds in memory (equals, comparisons, like, in, null checks, owner), for sources that simply fetch everything.
- **The store is wrapped, not special-cased.** `VirtualAwareRepository` sits in front of the real store: for a virtual DocType it reads from the source (returning only the columns asked for, as the real store does) and refuses every write with a clear 409; for every other DocType it forwards untouched. So lists, counts, the group-by sidebar, search, exports and reports work over virtual documents without each of them knowing, and a virtual DocType nobody supplies says so.
- **Roles still decide who may read.** The Desk is told a virtual DocType cannot be changed (no New, no Save, no Import) whatever the roles say; the server refuses writes either way.
- **A source for files is built in.** `Qahwa:VirtualDocTypes:<DocType> = path/to/file.json` serves a JSON array of objects (each with a unique text `name`; numbers become decimals; at most 5 MB; re-read when the file changes, at most once a second; a broken file gives an error to the person asking, never partial data). Other sources (an HTTP API, another database) are written as an `IVirtualSource` in an app or the host.

## Consequences
- Verified for real: the server ran against PostgreSQL with a virtual *Exchange Rate* DocType over a JSON file. The list, a filter, a single document and sorting worked, a write came back 409, no table was created, and the Desk list showed it with its title links and export buttons. Engine tests cover listing, filtering, paging, counting, grouping, projection, permissions, refused writes, a missing source, the definition rules, and that other DocTypes are unaffected; a PostgreSQL test checks that no table is made.
- Not yet: writes through a source, links *to* a virtual document being checked against the source when a document that links to it is saved (links to it are not validated against the source yet), filtering through tags, likes and table rows (refused with a message), sources that page or filter on their own server for very large data sets (a source can already do this itself, since it receives the query), per-user sources.
