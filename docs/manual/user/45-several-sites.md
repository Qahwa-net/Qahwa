# Running several sites in one server

One Qahwa server can serve several separate **sites**, for example one per company. Each site has its own database, users, documents, settings and files; nothing is shared. People reach a site by its own web address, and an account on one site does nothing on another.

## Making a site
```bash
qahwa new-site acme --sites ./sites --host acme.example.com --admin-connection "Host=db;Username=postgres;Password=…;Database=postgres" --email boss@acme.example.com
```
This creates the database `qahwa_acme`, the folder `sites/acme` with its `site_config.json`, the tables and the first administrator (you are asked for a password). Add `--connection-env ACME_DB` to keep the database password out of the file: the file then names an environment variable that you must set when the server runs.

Start the server with the sites folder:

```json
"Qahwa": { "Sites": { "Path": "sites" } }
```
(or the environment variable `Qahwa__Sites__Path`). `qahwa list-sites --sites ./sites` shows what the server will find. Restart the server after adding a site.

## The site file
```json
{
  "hosts": [ "acme.example.com", "*.acme.example.com" ],
  "connectionString": "Host=db;Database=qahwa_acme;Username=…;Password=…",
  "Qahwa": { "Auth": { "SignUp": { "Enabled": true } }, "Email": { "From": "no-reply@acme.example.com" } }
}
```
- `hosts`: the addresses of the site. `*.acme.example.com` covers one level (`shop.acme.example.com`, not `a.b.acme.example.com`).
- Anything else in the file replaces the shared setting for this site only. Emails sent for the site link to `https://` plus its first plain host unless you set `Qahwa:Auth:PublicUrl`.
- Files and scheduled backups of a site go in their own folders under `sites/<name>`.

## Good to know
- Every site needs its own database and its own host names; the server refuses to start if two sites share either, and says what is wrong.
- An address that no site claims shows the main site if the server has its own database, otherwise an "unknown site" message.
- Put a proxy with HTTPS in front, and make sure it passes the original `Host` header.
- Migrate one site with `qahwa migrate --site acme --sites ./sites`; the server migrates all sites at start when `Qahwa:AutoMigrate` is on.
