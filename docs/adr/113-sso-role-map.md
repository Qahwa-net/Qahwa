# ADR-113: Roles from a sign-in provider (restricted)

Status: accepted. Replaces the "no roles come from the token" rule of ADR-051 only for roles an administrator names explicitly.

## Decision
A provider in `Qahwa:Auth:Sso:Providers:<name>` may set `GroupsClaim` (the id-token claim that lists groups, for example `groups`) and `RoleMap` (provider group → list of Qahwa roles). At every sign-in through that provider:
- the roles mapped to the person's groups are **granted**;
- the other roles named anywhere in the map are **taken away** (those roles are managed by the provider);
- every role **not named in the map is never touched**.

Safeguards:
- `Administrator`, `System Manager`, `All` and `Guest` can **never** be named in a map. A map that names one makes the provider refuse every sign-in (`misconfigured`) instead of guessing; so does a groups claim without groups or groups without a claim.
- The map lives in server configuration, not in the Desk, so only whoever controls the server can change it (the same reasoning as ADR-107).
- The token is still fully validated first (signature, issuer, audience, nonce, expiry). At most 200 groups of at most 200 characters are read; anything else is ignored. Without a map the token's groups are ignored completely.
- Grants and removals are ordinary Has Role documents, so they appear in the updates log.

## Consequences
- Whoever runs the outside directory can give a person any role named in the map, and cannot give the top roles.
- Roles a person was given by hand are only affected if the same role is also in the map.
