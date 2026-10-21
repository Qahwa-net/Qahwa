# ADR-115: Browser end-to-end tests

Status: accepted. Replaces the "browser end-to-end tests are not built" limit of ADR-107.

## Decision
`e2e/` is a small Playwright project (its own `package.json`, so the Desk build and its unit tests are untouched) that drives the Desk in a real Chromium against a running site. It covers the paths that unit tests cannot: a wrong password stays on the sign-in page, the home page groups DocTypes by module, a customer made through the form appears in the list, unsaved typing comes back after a reload and can be discarded, and `?` and Alt+N work with real key presses.

Run it against a **throwaway** site (never production): set `QAHWA_E2E_URL` (default `http://localhost:5099`), `QAHWA_E2E_EMAIL` and `QAHWA_E2E_PASSWORD` in the environment, then `npm install`, `npx playwright install chromium` and `npm test` inside `e2e/`. Without the email and password every test is skipped, so the folder is safe to leave in a checkout. The password is read from the environment only; it is never stored in a file.

## Consequences
The tests create data on the site they run against (customers) and are not part of `dotnet test` or CI by default: they need a database, a running server and a downloaded browser. Add them to a pipeline that has those.
