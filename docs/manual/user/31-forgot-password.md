# Forgot your password

1. On the sign-in page choose **Forgot your password?** and enter your email.
2. If that address has an account you will get an email with a link. The page always says the same thing, so it never tells anyone who has an account.
3. Open the link **within one hour** and choose a new password. A link works once.
4. You are signed out everywhere else, and can sign in with the new password. If you use two-factor sign-in you still need your code.

You can ask for at most three links an hour.

## Choosing a password
At least 10 characters. It cannot be your email, a very common password (`password1234`, `qwertyuiop`), a simple sequence (`1234567890`), a few repeated characters, or mostly your own name. A few random words joined together is a good password.

## For the person who runs the system
Set the public address of the site, or no email is sent:

```json
"Qahwa": { "Auth": { "PublicUrl": "https://qahwa.example.com" }, "Email": { "Host": "smtp.example.com", ... } }
```

## When your password expires
If the people who run the system have set a time limit for passwords (for example 90 days), signing in with an old password shows **Your password has expired**. Enter the current password, then a new one that is different and strong, and sign in again. Nothing else can be done until you do. They set it with `"Auth": { "PasswordMaxAgeDays": 90 }`.
