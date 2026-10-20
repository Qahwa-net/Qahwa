# Letting other applications use your account (OAuth)

## If an application asks for access
You are sent to a page that says **who is asking**, **what it would be allowed to do** (see your name and email; read your data; change your data) and **where you will be sent next**. Choose **Allow** or **Deny**. An application can never do more than you can do yourself, and it never sees your password.

To take access back, open **My account → Applications you allowed** and choose **Withdraw access**. The application stops working at once. Access also ends if your account is disabled.

## For administrators
Open **Tools → OAuth Applications** (System Manager):

1. Give the application a name and its **redirect addresses**, one per line. They must be `https` addresses (or `http://localhost…` for a program on a computer). The address the application sends people to must match one of them exactly.
2. Tick what it may ask for: *Name and email*, *Read data*, *Change data*.
3. Choose **Public client** only for a mobile or browser app that cannot keep a secret. Otherwise copy the **client secret** when it is shown; it is not shown again.
4. **Register**. Give the application its **Client ID** and secret.

Removing an application withdraws every access it was given. Applications must use the authorization-code flow with PKCE (S256); the addresses they need are listed at `/api/oauth/metadata`.

For developers: send people to `/oauth/authorize?client_id=…&redirect_uri=…&scope=profile api&state=…&code_challenge=…&code_challenge_method=S256`, exchange the code at `POST /api/oauth/token` (`grant_type=authorization_code`, `code`, `redirect_uri`, `code_verifier`, and the client credentials in the form or as HTTP Basic), call the API with `Authorization: Bearer <access_token>`, renew with `grant_type=refresh_token`, and read the person at `GET /api/oauth/userinfo`.
