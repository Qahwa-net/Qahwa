# Docker and deployment

## One command

```bash
docker compose up --build
```

This builds the image from the [Dockerfile](../../../Dockerfile) (the Desk is built first, then the server is published with it) and starts two containers:

| Service | What it is | Data kept in |
|---|---|---|
| `postgres` | PostgreSQL 17 (user and database `qahwa`) | volume `qahwa-pgdata` |
| `app` | The Qahwa server with the Desk, listening on port 8080 | volume `qahwa-data` (uploaded files) |

Open http://localhost:8080 and sign in with `admin@example.com` and `ChangeMe-Qahwa-2026`. The first start takes a few minutes (it compiles everything); later starts take seconds. The app waits for the database to be healthy, brings the schema up to date (`Qahwa__AutoMigrate`), loads the sample DocTypes and makes the administrator.

Run it in the background with `docker compose up --build -d`, follow the log with `docker compose logs -f app`, stop it with `docker compose down` (the data stays) and remove the data too with `docker compose down -v`.

## Settings (a `.env` file next to `docker-compose.yml`)

| Variable | Default | Meaning |
|---|---|---|
| `QAHWA_PORT` | `8080` | The port on your machine |
| `QAHWA_ADMIN_EMAIL`, `QAHWA_ADMIN_PASSWORD` | `admin@example.com`, `ChangeMe-Qahwa-2026` | The first administrator (10+ characters). Made once, only when that account does not exist; changing the variable later does not change the password |
| `QAHWA_DB_PASSWORD` | `qahwa_dev_only` | The database password. Set it **before the first start**: PostgreSQL stores it in its volume |
| `QAHWA_DB_PORT` | `5432` | The database port on your machine (bound to `127.0.0.1` only) |

Any setting from [Getting started](01-getting-started.md#run-the-app) can be given to the `app` service as an environment variable, with `__` for each `:` (for example `Qahwa__Email__Host`, `Qahwa__Files__Provider`).

### The first administrator

The server reads `Qahwa:Bootstrap:AdminEmail`, `AdminPassword` and `AdminName` at start (ADR-162). If an account with that email exists nothing happens; otherwise it is made with the *Administrator* and *System Manager* roles. After the first sign in, change the password in **My account** and remove the variables from the file.

## Only the database

For `dotnet run` development you do not need the app container:

```bash
docker compose up -d postgres
```

## Updating

```bash
git pull
docker compose up --build -d
```

The new image starts, `AutoMigrate` adds new tables and columns (it never drops data), and one-time patches run. Take a backup first (**Backup & Restore** in the Desk, or `qahwa backup`).

## Going beyond a trial

The compose file is a trial setup: plain http, a default password, one server. For a real site:

1. **Use https.** Put a reverse proxy (Caddy, nginx, Traefik) or your cloud load balancer in front, and **remove `Qahwa__Auth__AllowHttp`** so the sign-in cookie is only sent over https. Configure forwarded headers so the server sees the real address and scheme.
2. **Change every default**: the administrator password and `QAHWA_DB_PASSWORD`; do not publish the database port (delete the `ports` lines of `postgres`).
3. **Back up both volumes**: the database (`pg_dump`, or the Desk's backup) and `qahwa-data` (uploaded files). Or keep files in object storage (`Qahwa__Files__Provider` = `s3` or `azure`, ADR-145, ADR-149).
4. **Run more than one server?** Share the Data Protection key ring and keep files in object storage; only one server needs the scheduler (`Qahwa__Scheduler__Enabled=false` on the others).
5. **Mail**: set `Qahwa__Email__Host`, `Port`, `User`, `Password` and `From`; without a host messages are only logged.
6. **Several sites on one server**: see the site manager (ADR-148) and `qahwa new-site`.

## Building the image yourself

```bash
docker build -t qahwa .
docker run -p 8080:8080 \
  -e ConnectionStrings__Default="Host=<db host>;Database=qahwa;Username=qahwa;Password=<password>" \
  -e Qahwa__Bootstrap__AdminEmail=you@example.com -e Qahwa__Bootstrap__AdminPassword=<10+ characters> \
  -e Qahwa__Auth__AllowHttp=true \
  qahwa
```

The image runs as a non-root user, listens on 8080, has a health check on `/api/health`, keeps files in `/app/data` and loads DocTypes from `/app/samples/doctypes` (point `Qahwa__DocTypePaths__0` at your own folder, mounted into the container, to use your app's DocTypes).
