# ADR-084: Boot hooks

Status: accepted

## Context
An app's pages often need a few facts as soon as the Desk starts (a company name, a feature switch, whether the person may approve). Asking for each with its own request slows every page and scatters the code.

## Decision
- An app can register one **boot hook** (`IAppBuilder.Boot`). It receives the signed-in person and a permission-checked `DocumentService` and returns a small object.
- `GET /api/boot` (signed-in people only; never cached) returns `{ data: { apps: { <app>: <object> } } }`. The Desk fetches it once per session (`useBoot`, `useAppBoot<T>(app)`).
- **Limits and failure:** each answer is limited to 16 KB; a hook that throws, or whose answer is too large or not JSON, is left out and written to the Error Log with the app's name, and the other apps are unaffected. One hook per app (registering a second stops the server at start-up).
- The data is per person, so it never contains more than that person may see if the hook reads through the supplied `Documents`.

## Consequences
- Boot hooks run for every start-up of every person: keep them fast and small.
- The hooks do not run on token or API-key calls unless they call `/api/boot` themselves.
