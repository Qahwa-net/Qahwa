# Where you are signed in

**My account → Where you are signed in** lists every browser or phone that is signed in as you: what it is, its address, when it signed in and when it was last used. The one you are using now is marked **this device**.

- **Sign out** next to a place ends that sign-in at once (within a few seconds), for example a phone you lost.
- **Sign out of all other places** ends every sign-in except this one.
- Changing your password also signs you out everywhere else.
- A sign-in ends by itself 14 days after it began, even if you use it every day.

If you see a place you do not recognise: sign it out, change your password and turn on two-factor sign-in.

## For a System Manager
You can sign someone out everywhere (for example when they leave): send `POST /api/auth/sessions/revoke-user` with `{"user": "name@example.com"}`.
