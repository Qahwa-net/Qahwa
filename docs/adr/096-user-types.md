# ADR-096: User types (System User and Website User)

Status: accepted

## Context
Customers and visitors who sign in to a portal must never be able to act as staff, even if someone gives them a staff role by mistake or an old role grant lingers.

## Decision
- `User` has a `user_type`: **System User** (default, roles as granted) or **Website User**.
- When a session is loaded (`AuthService.LoadSessionAsync`), a Website User gets **only** the `Website User` role (plus `All`), whatever Has Role rows, profiles or grants exist. The rule is applied on every request, so changing the type takes effect at once and nothing about the role rows has to be cleaned up.
- DocTypes meant for portals (web forms, portal pages) grant permissions to the `Website User` role.

## Consequences
- A Website User cannot reach staff features because no staff role is ever loaded for them; the Desk still opens but shows nothing they may use.
- Module-level portals and a separate sign-in page for Website Users are future work.
