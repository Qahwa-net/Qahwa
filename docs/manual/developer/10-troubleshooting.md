# Troubleshooting

## Starting

| Symptom | Cause and fix |
|---|---|
| `docker compose up` says the port is in use | Another program has 8080 (or 5432). Set `QAHWA_PORT` / `QAHWA_DB_PORT` in a `.env` file |
| The app container restarts and the log says it cannot connect | The database is not ready or the password differs. The password is stored in the `qahwa-pgdata` volume at first start: use the same `QAHWA_DB_PASSWORD`, or `docker compose down -v` to start again (this deletes the data) |
| The page loads but sign in does nothing or comes straight back | The sign-in cookie is only sent over https unless `Qahwa__Auth__AllowHttp=true` (the compose file sets it for http trials) |
| Cannot sign in as the administrator | The account is only made when it does not exist; changing `QAHWA_ADMIN_PASSWORD` later does nothing. Reset it with `qahwa create-admin --connection "..." --email you@example.com` |
| `The password is too short` / the password is refused | 10 or more characters, and not the email or name |
| The server stops at start with a message about LDAP, SAML or IP lists | A mistake in that setting; the server refuses to start half configured. Fix the setting named |
| `dotnet run` cannot connect to the database | Start it: `docker compose up -d postgres`; the connection string is in `appsettings.Development.json` |
| The Desk is blank or 404 on `/` with `dotnet run` | Build it once: `cd src/Qahwa.Desk && npm install && npm run build` (or use `npm run dev` on port 5173) |

## Using it

| Symptom | Cause and fix |
|---|---|
| A new DocType does not show in the sidebar | It is in a module you cannot see, or you have no read permission on it. Check **Role Permissions**; log out and in so roles are refreshed |
| `permission_denied` from the API | The key's owner lacks the right, or the key is read-only (only GET works). A key acts with its owner's current permissions |
| `validation_error` | `details` names each field; child table rows are `items[0].qty` |
| `conflict` on save | Someone else saved first: reload, or send the `modified` value you read |
| Saving a DocType says a change is blocked | The **Changes** tab of the Designer explains: for example a type change that would lose data. Make a new field instead |
| My app is not on the Apps screen | It must be built into the server: a project reference in `Qahwa.Server.csproj` and `AddQahwaApp<...>()` in `SiteServices.cs`, then rebuild and restart. See [Building and installing an app](11-building-and-installing-apps.md#checklist-when-it-does-not-show-up) |
| A scheduled job did not run | **Scheduled Jobs** shows the last run and any error; *Run now* runs it once. Is `Qahwa:Scheduler:Enabled` on? |
| Email is not arriving | Without `Qahwa:Email:Host` messages are only logged. With it, check the log for the mail server's answer |
| A list is slow | **Slow requests** shows the address and the number of database statements; hundreds of statements usually means a link title lookup per row or a report over unindexed fields (tick *Index* on the field) |

## Looking closer

- `docker compose logs -f app` or the console of `dotnet run` shows start-up, schema changes and errors; set `Logging__LogLevel__Default=Debug` for more.
- `GET /api/health` answers `{"status":"ok"}` when the server is up.
- `qahwa migrate --plan` prints what the schema sync would do without doing it.
- The **API** tab of the DocType Designer gives a ready `curl` call for each DocType, and `/api/openapi.json` lists every call.
