# Backup and restore

**Backup & Restore** (Administrator only, under Tools).

## Download a backup
Choose **Download a backup**. You get a zip with all your documents and everything you built in the Desk (DocTypes, customizations, workflows, rules). Keep it safe: it holds all your data.

**Not in the backup:** passwords, two-factor secrets and sign-ins (people set a password again, or use "Forgot your password"), the contents of uploaded files (copy the files folder separately), and queued emails and logs.

## Restore
1. Choose the backup file. A **preview** shows how many documents would be added to each DocType.
2. Choose **Restore what is missing**.

Restoring **only adds**. Anything that already exists is left as it is; nothing is overwritten or deleted. To bring a site back, restore into an empty system, then create an administrator with the command line (`create-admin`) and let people reset their passwords.

## Every day by itself
Whoever runs the system can have a backup written every day to a folder:

```json
"Qahwa": { "Backup": { "Path": "D:/backups/qahwa", "Keep": 7 } }
```

The newest 7 are kept. Failures appear in the Error Log. For recovery to an exact moment, also use your database's own backup tools.
