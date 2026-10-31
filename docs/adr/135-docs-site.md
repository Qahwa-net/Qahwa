# ADR-135: A documentation website built from the markdown

**Status:** accepted (developer experience)

## Context
The user guide, developer manual, reference pages and 134 decision records are markdown files. They read well on GitHub but have no search, no single navigation and cannot be hosted on their own.

## Decision
- `docs/site` holds a small build script (`node build.mjs`, using `marked`) that turns every `docs/**/*.md` page and the repository `README.md` into a static site in `docs/site/dist` (ignored by git). `npm run build` writes the site; `npm run check` only checks the links.
- **One navigation for all pages**, grouped as Start, User guide, Developer manual, Reference and Architecture decisions, with a filter box that narrows the list as you type. No server and no JavaScript framework: the pages are plain HTML and work from a folder or any static host. Light and dark themes follow the browser; the layout works on a phone.
- **Links between pages are checked.** A link to a `.md` page that does not exist fails the build (the check found none after pages that write repository-root paths such as `docs/adr/…` were accepted). Links to other files point at the repository on GitHub.
- The script has no dependency on the .NET build.

## Consequences
- Not yet: full-text search across page bodies, a publishing step in CI, and versioned docs for each release.
