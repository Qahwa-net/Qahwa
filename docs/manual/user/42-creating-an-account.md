# Creating your own account

If your organisation allows it, the sign-in page has a **Create an account** link.

1. Enter your name and your email address, and press **Send the link**.
2. Open the email we send you and press the link in it. It works for 24 hours and only once.
3. Choose a password (at least 10 characters; not a common word, your name or a simple pattern), and press **Create my account**.
4. Sign in.

Good to know:
- The account is only made after step 3. If you did not ask for it, ignore the email: nothing was made.
- If you already have an account, the email says so and shows where to sign in or reset your password. The page itself always gives the same answer, so nobody can use it to find out who has an account.
- Names can have letters (any language), numbers, spaces and `. - ' ,` only.
- You can ask for at most three emails an hour for one address.
- A new account starts with only the access your organisation decided on; an administrator can give you more.

## For the person who runs the system
Sign-up is off until you switch it on. The link in the email needs the public address of the site.

```json
"Qahwa": { "Auth": { "PublicUrl": "https://qahwa.example.com",
  "SignUp": { "Enabled": true, "AllowedDomains": [ "example.com" ], "DefaultRoles": [ "Customer" ], "LinkHours": 24 } } }
```

- `AllowedDomains`: leave empty to let any address sign up; list domains (exactly, `example.com` does not include `mail.example.com`) to limit it to your own people.
- `DefaultRoles`: the roles new accounts get. The Administrator role is ignored here.
- Qahwa must be able to send email (see *Email*), otherwise nobody gets the link.
