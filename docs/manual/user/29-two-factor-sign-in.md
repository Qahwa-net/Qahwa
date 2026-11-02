# Two-factor sign-in

With two-factor sign-in, signing in needs your password **and** a 6-digit code from an authenticator app on your phone (Google Authenticator, Microsoft Authenticator, Authy, 1Password and others).

## Turn it on
1. Open **My account → Two-factor sign-in → Set up**.
2. Scan the QR code with the app (or type the key shown below it).
3. Enter the 6-digit code the app shows and choose **Turn on**.
4. **Save the recovery codes.** You get 10, shown once. Each works once if you lose your phone. Download them or print them and keep them safe.

## Signing in
Enter your email and password, then the code from the app. If your phone is lost, type a recovery code instead of the 6 digits.

A code can only be used once; if it is refused, wait for the next one. Five wrong codes in a row lock the account for 15 minutes, like wrong passwords.

## Turn it off
**My account → Two-factor sign-in**: enter your password and a code, then **Turn off**.

## Lost phone and recovery codes
Ask a System Manager to reset your two-factor sign-in. After that your password alone signs you in, and you can set it up again.

## Passkeys: sign in without a password
A **passkey** lets you sign in with your fingerprint, face or device PIN. It is stronger than a password: it only works on this site (a look-alike site gets nothing), there is nothing to type, and nothing secret to steal from the server. A passkey sign-in already proves who you are, so no code is asked after it.

**Add one:** **My account → Passkeys → Add a passkey**, give it a name (for example "Work laptop"), enter your password once, and follow what your device asks. Add one on each device you use; you can have up to 10.

**Use one:** on the sign-in page choose **Sign in with a passkey** and confirm on your device.

**Remove one:** **My account → Passkeys**, the bin icon. Do this when you lose a device. Your password still works, so keep it, and keep your two-factor method if you use one.

Notes for administrators: passkeys are on by default and each person chooses to add them. Set `Qahwa:Auth:Passkeys:Enabled` to `false` to turn them off. Passkeys are tied to the site's address: set `Qahwa:Auth:PublicUrl` (the address people type) so the server does not have to guess it, or `Qahwa:Auth:Passkeys:RpId` and `Origins` when people reach the site by more than one address. Changing the domain makes existing passkeys stop working, because the browser ties them to the old one. People whose accounts come from the company directory (LDAP) cannot add passkeys yet.
