# ADR-051: Single sign-on with OpenID Connect

**Status:** accepted (L10)

## Context
Organisations want people to sign in with the company identity (Microsoft Entra, Google Workspace, Keycloak, Okta…) instead of another password. Every one of these speaks OpenID Connect. The risks are well known: a forged callback signing in the wrong person, tokens accepted from the wrong issuer or audience, an unverified email used to take over an account, and open redirects after sign-in.

## Decision
- **Authorization-code flow with PKCE (S256), `state` and `nonce`.** `SsoService.StartAsync` makes all three, and puts them (with the provider name and an expiry of 10 minutes) into an **encrypted** value kept in an HttpOnly, SameSite=Lax, `Secure` cookie (`qahwa.sso`, path `/api/auth/sso`). The callback needs that cookie *and* a `state` in the address that matches it, so a callback started by someone else (login CSRF) is refused. The cookie is deleted on every callback, so it works once.
- **The ID token is checked fully** (`JsonWebTokenHandler`): signature against the provider's published keys (asymmetric algorithms only, never `none` and never HMAC), issuer equals the issuer the provider announced *and* the configured authority, audience equals our client id, lifetime, `nonce`, and `sub` present. If the signature fails, the keys are fetched once more (key rotation) and the token is checked again.
- **Who the person is.** First by the provider's own id: `(issuer, sub)` in `__identities`, which survives email changes. Otherwise by email, and only when the provider says the email is **verified** (or the provider is configured `TrustEmail`) and, if `AllowedDomains` is set, the domain matches exactly. A user who is already linked to a *different* `sub` at that issuer is refused, so a second identity with the same email cannot take over an account. A **disabled** account is never switched on by signing in. Unknown people get an account only if the provider has `AutoCreate` **and** `AllowedDomains`, with only the roles in `DefaultRoles` (roles in the token are never read). Such accounts, and accounts without a password, get a random password nobody knows.
- **The server calls the provider carefully** (`HttpOidcBackchannel`): the address comes only from configuration; HTTPS only (http only for localhost); no redirects followed; 10-second limit; answers over 1 MB are refused; discovery and keys are remembered for ten minutes; the issuer in the discovery answer must be the configured one.
- **Where people land.** `next` must be a path on this site (`/…`, never `//`, `/\`, a scheme, or control characters), else `/`. Errors go back to `/login?sso_error=<short code>`; the reason is only logged.
- **Redirect address** is built from `Qahwa:Auth:PublicUrl` (only in Development the request's own address), never from the Host header in production. The provider also has this exact address registered.
- A session is made the same way as for a password (listed under "Where you are signed in", ended by sign-out). Two-factor is **not** asked again: the provider has already done its own checks. Start and callback are rate limited like sign-in.
- **Configuration** (`Qahwa:Auth:Sso:Providers:<name>`): `DisplayName`, `Authority`, `ClientId`, `ClientSecret` (keep it in an environment variable or secret store), `Scopes`, `AllowedDomains`, `AutoCreate`, `DefaultRoles`, `TrustEmail`. Names are letters, digits, dash and underscore.
- **Endpoints:** `GET /api/auth/sso`, `GET /api/auth/sso/{provider}/start?next=`, `GET /api/auth/sso/{provider}/callback`.

## Consequences
- No logout at the provider (RP-initiated logout) and no refresh tokens: the Qahwa session lives by its own rules.
- Roles and groups from the provider are not mapped yet; people get `DefaultRoles` or the roles an administrator gave them.
- SAML and LDAP are separate work; Qahwa as an OAuth *provider* for other apps is not done.
