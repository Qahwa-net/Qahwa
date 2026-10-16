# ADR-054: Sign-in with a company directory (LDAP / Active Directory)

**Status:** accepted (L12)

## Context
Many organisations keep people and passwords in Active Directory or another LDAP directory and want the same email and password to work here, without a second password. OpenID Connect (ADR-051) covers modern providers; LDAP covers the rest. The classic mistakes: building the search filter by pasting in what the person typed (LDAP injection), an empty password making a bind "anonymous" and succeeding, sending passwords in clear text, and keeping old access alive after someone is removed from the directory.

## Decision
- **Two steps.** The person types their email and directory password. A read-only service account (`BindDn`) searches under `BaseDn` with `UserFilter` (default `(&(objectClass=person)(mail={0}))`); then the directory is asked to **bind as the found entry** with the typed password. Qahwa never sees a hash and stores nothing of the password.
- **No injection:** the email is escaped for filters (RFC 4515: `\ * ( )` and NUL) before it is put into `{0}`, and the filter must contain `{0}` (checked at start). The distinguished name used to bind comes from the directory's answer, never from the person.
- **Exactly one match, same email:** zero or several entries means no sign-in, and the entry's email must equal what was typed.
- **Never empty:** empty passwords, passwords longer than 128 characters or with a NUL, and malformed emails are refused before the directory is contacted (an empty password is an anonymous bind on many servers). The real client also refuses an empty password itself.
- **Safe transport, fail-loud:** LDAPS (`UseTls`, default, port 636) or STARTTLS (`StartTls`) with the system checking the certificate; plain LDAP is only allowed for a server on this machine. Settings are checked when the server starts and a bad combination stops it with a clear message (`LdapService.Problem`). No referrals are chased; every call has a time limit (`TimeoutSeconds`, default 10).
- **Accounts:** like single sign-on, the account is found by the email; a **disabled** account is never switched on; a person already tied to a *different* directory entry is refused; unknown people get an account only with `AutoCreate` **and** `AllowedDomains`, with only `DefaultRoles`. The local password is random and unknown. The first successful sign-in records `(ldap://server, DN)` in `__identities`.
- **The directory is the authority for those people.** Once recorded, a person can only sign in through the directory (the local password no longer works, and the password-expiry rule does not apply, since it governs a password nobody uses), so removing or disabling them there ends their access here the next time they sign in; set the session lifetime accordingly. Everyone else still uses their password here, and when that fails the directory is tried (which links them).
- Two-factor, sessions, rate limiting and the IP allow-list apply as for any sign-in.

## Consequences
- The directory client (`LdapDirectory`, System.DirectoryServices.Protocols) is covered by tests only through a stand-in directory; it has not been run against a live server here. It needs `libldap` on Linux.
- No group-to-role mapping and no background sync: roles come from `DefaultRoles` or an administrator; people removed from the directory keep an open session until it ends.
- Nested searches use the subtree of `BaseDn`; a filter returning more than one entry is a refusal rather than a choice.
