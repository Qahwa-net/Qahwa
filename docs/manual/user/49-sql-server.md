# Using SQL Server

Qahwa runs on PostgreSQL (the default) or on SQL Server 2017 or later. A server (or, with several sites, each site) uses one of them.

## One site
```json
"ConnectionStrings": { "Default": "Server=db.example.com;Database=qahwa;User Id=qahwa;Password=…;Encrypt=True" },
"Qahwa": { "Database": { "Provider": "SqlServer" } }
```
Create an empty database first, then make the tables and the first administrator:

```bash
qahwa migrate --provider sqlserver --connection "Server=db.example.com;Database=qahwa;User Id=qahwa;Password=…;Encrypt=True"
qahwa create-admin --provider sqlserver --connection "…" --email boss@example.com
```
(or set `Qahwa:AutoMigrate` to true and let the server make the tables when it starts).

## A site on SQL Server next to others
```bash
qahwa new-site acme --provider sqlserver --sites ./sites --host acme.example.com \
  --admin-connection "Server=db.example.com;User Id=sa;Password=…;Initial Catalog=master" --email boss@acme.example.com
```
This creates the database `qahwa_acme`, the site folder and the tables. The site's `site_config.json` records that it uses SQL Server, so a server can run PostgreSQL sites and SQL Server sites together (see *Running several sites in one server*).

## Good to know
- The login needs permission to create tables and indexes in its database (`db_owner` is simplest; `db_ddladmin` plus read and write rights is enough).
- Search is not case-sensitive if the database collation is not (the usual default). In a search pattern `%` and `_` are wildcards, and so are `[` and `]` in SQL Server.
- A list filter may name at most 2,000 values on SQL Server.
- Use short text fields (not long text) for fields that are indexed or must be unique.
- Back up with your usual SQL Server tools, or with the Backup page, which works the same on both.
