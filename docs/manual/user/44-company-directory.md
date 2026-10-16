# Signing in with your company directory password

If your organisation connects Qahwa to its directory (Active Directory or another LDAP directory), you sign in on the normal page with your **email and the password you use at work**. There is nothing else to do the first time: if you are allowed, your account is made or linked.

- After your first sign-in this way, only your directory password works here. If you change it at work, use the new one; if you are removed from the directory, you can no longer sign in.
- Two-factor sign-in, if you turned it on, still asks for its code.
- Qahwa does not keep your directory password.
- If the directory cannot be reached, sign-in fails; try again later.

## For the person who runs the system
Add this to the configuration (the service account only needs to read people):

```json
"Qahwa": { "Auth": { "Ldap": {
  "Enabled": true,
  "Server": "ad.example.com",
  "Port": 636,
  "UseTls": true,
  "BindDn": "CN=qahwa-reader,OU=Service,DC=example,DC=com",
  "BaseDn": "OU=People,DC=example,DC=com",
  "UserFilter": "(&(objectClass=user)(mail={0}))",
  "AllowedDomains": [ "example.com" ],
  "AutoCreate": true,
  "DefaultRoles": [ "Employee" ]
} } }
```

Keep the service account's password out of the file: set the environment variable `Qahwa__Auth__Ldap__BindPassword`.

- `{0}` in `UserFilter` is replaced by the email the person typed. For Active Directory you may prefer `userPrincipalName={0}`; the entry's `EmailAttribute` (default `mail`) must still equal what was typed.
- Use `UseTls` (LDAPS, port 636) or `StartTls` (port 389). Plain LDAP is refused except to a server on the same machine. The server starts only if the settings are sound and says what is wrong if not.
- `AutoCreate` makes accounts for directory people at the `AllowedDomains`; leave it off to add people yourself first.
- Install `libldap` (for example `apt install libldap-2.5-0`) when running on Linux.
