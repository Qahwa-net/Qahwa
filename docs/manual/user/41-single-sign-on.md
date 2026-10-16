# Signing in with your company account

If your organisation has set it up, the sign-in page shows a button such as **Sign in with Company login** under the password form. Press it, sign in at your company's page, and you come back to Qahwa signed in, on the page you were going to.

- You need an account in Qahwa (an administrator adds you), unless your organisation allows accounts to be made automatically for its own email addresses.
- Your email address must be confirmed by the company login. If it is not, you will see *"Your provider has not confirmed your email address."*
- If your account is switched off you cannot come in this way either.
- Once you have signed in this way, Qahwa knows you by your company identity, so a change of your email address there does not lock you out. Nobody else with the same email address but a different identity can use your account.
- Two-factor codes are not asked again; your company login has already checked you.
- The sign-in shows up under **Where you are signed in** like any other, and you can end it there.

When something goes wrong you return to the sign-in page with a short message (for example "There is no account for you here"). Tell your administrator what it said.

## For the person who runs the system
Register Qahwa at your identity provider as a web application with the redirect address `https://<your-qahwa>/api/auth/sso/<name>/callback` (the `<name>` is your choice, for example `company`). Then, in the configuration:

```json
"Qahwa": { "Auth": { "PublicUrl": "https://qahwa.example.com",
  "Sso": { "Providers": { "company": {
    "DisplayName": "Company login",
    "Authority": "https://login.example.com/realms/main",
    "ClientId": "qahwa",
    "AllowedDomains": [ "example.com" ],
    "AutoCreate": false,
    "DefaultRoles": [ "Employee" ]
} } } } }
```

Keep the secret out of the file: set the environment variable `Qahwa__Auth__Sso__Providers__company__ClientSecret`.

`Authority` is the address whose `/.well-known/openid-configuration` describes the provider; it must be `https`. `AutoCreate` only works together with `AllowedDomains`. `TrustEmail` is for providers that do not say whether an email is confirmed; leave it off unless you know the provider checks every address.
