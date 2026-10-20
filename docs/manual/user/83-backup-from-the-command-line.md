# Backup and restore from the command line (for administrators)

Run these on the server:

```
qahwa backup --site mysite --sites /srv/qahwa/sites --out /backups/mysite.zip --doctypes /srv/qahwa/doctypes
qahwa restore /backups/mysite.zip --site mysite --sites /srv/qahwa/sites --doctypes /srv/qahwa/doctypes --dry-run
```

- A backup holds your business data and definitions, never passwords, sessions or secrets. Keep the file private.
- `backup` will not overwrite a file that already exists.
- `restore` only adds what is missing; rows that exist are left alone. Use `--dry-run` first to see what would be added. People set their password again after restoring into a new site.
