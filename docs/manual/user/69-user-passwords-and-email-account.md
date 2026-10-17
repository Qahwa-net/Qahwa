# User passwords and outgoing email

System Managers and Administrators can open **User**, choose a user, then use the lock icon to set a new password or the mail icon to send a reset link. Setting a password applies the existing password policy and ends the user's sessions. Only Administrators may manage passwords of users holding the Administrator role. Password operations are recorded in Access Log without passwords or reset tokens.

Reset emails work for enabled users, including newly created users who have not chosen a password. Links expire after one hour, cannot be reused after the password changes, and are bound to the user record's creation. Requests are limited to three links per user per hour. Disabled users cannot use reset links.

Open **Tools → Email Account**:

1. Enter the sender email address, SMTP server, port, username and password/app password.
2. Enable outgoing email. For STARTTLS use port 587 and enable **Use STARTTLS**. Port 465 implicit TLS is not supported by this transport.
3. Set the public site URL used in email links. `Qahwa:Auth:PublicUrl`, if configured on the server, takes precedence.
4. Save, then use the mail icon to send a test email.

This is one site-wide outgoing account; incoming mail and OAuth SMTP authorization are not implemented. Existing host configuration under `Qahwa:Email` remains a fallback until an Email Account is saved. A saved disabled account disables outgoing delivery. All outgoing messages, including queued document emails and password resets, use the current saved account without a restart. When no SMTP account is configured, sending fails instead of reporting a log-only message as delivered; queued messages use the existing retry behavior.

SMTP passwords use the framework's encrypted Password fields. API responses mask them, and normal backups omit Email Account. Keep the site's ASP.NET Data Protection keys persistent across restarts and deployments. Configure the account separately after restoring a backup.

API (System Manager/Administrator):

- `POST /api/users/{email}/password` with `{ "newPassword": "..." }`
- `POST /api/users/{email}/reset-password` with `{}`
- `POST /api/email/test` with `{ "to": "person@example.com" }`

The Email Account itself uses the existing single-document API and permission engine at `/api/resource/Email%20Account/Email%20Account`.
