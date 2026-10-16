# Signing in with SAML

If your organisation uses SAML (for example ADFS, Okta, Entra ID, Shibboleth), the sign-in page shows a button such as **Sign in with Company SAML**. Press it, sign in at your organisation's page, and you come back to Qahwa signed in, on the page you were going to. It works like the company account sign-in described in *Signing in with your company account*: you need an account (or your organisation lets accounts be made for its own email addresses), a switched-off account stays off, and two-factor codes are not asked again.

If something is wrong you return to the sign-in page with a short message; tell your administrator what it said.

## For the person who runs the system
1. Give your identity provider's administrator the **metadata** of this application: `https://<your-qahwa>/api/auth/saml/<name>/metadata` (the `<name>` is your choice, for example `company`). It holds the entity id and the address the provider posts its answer to (`…/api/auth/saml/<name>/acs`).
2. Take from the provider its **entity id**, its **sign-in address** and its **signing certificate**, and add them to the configuration:

```json
"Qahwa": { "Auth": { "PublicUrl": "https://qahwa.example.com",
  "Saml": { "Providers": { "company": {
    "DisplayName": "Company SAML",
    "IdpEntityId": "https://login.example.com/saml",
    "SsoUrl": "https://login.example.com/saml/sso",
    "Certificates": [ "MIIC...base64 of the certificate..." ],
    "EmailAttribute": "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
    "AllowedDomains": [ "example.com" ],
    "AutoCreate": false,
    "DefaultRoles": [ "Employee" ]
} } } } }
```

- `Certificates` may list two certificates while the provider changes its key. Only these are used to check signatures.
- `EmailAttribute` is optional; the usual names (`email`, `mail`, the Microsoft claim, …) are tried, then a NameID that is an email address.
- The provider must **sign** its answers (the assertion or the whole response) with RSA SHA-256 or stronger and send the assertion unencrypted.
- The server will not start if a provider's settings are incomplete or unsafe, and says what is wrong.
- Use `https` on the public address. SAML needs the browser to send a cookie with a form posted from the provider's site, which browsers only allow over https (while testing on a local machine over plain http it works only when the provider is on the same machine).
