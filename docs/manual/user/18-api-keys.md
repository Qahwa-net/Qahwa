# API keys: let a program act as you

An **API key** lets a script or another system read or change documents as you, without knowing your password. A key can do **exactly what you can do**, no more. If your permissions change, so do your keys'.

## Making a key

1. Open **My account** (your name at the top right).
2. Under **API keys** click **New key**.
3. Describe what it is for ("Nightly report script"). Optionally set an expiry date. Tick **Read only** if the program only needs to look: it can then read but never change or delete anything.
4. Click **Make key**. The key is shown **once**. Copy it now and keep it as secret as a password; it cannot be shown again. If you lose it, delete it and make a new one.

## Using a key

Send it in a header with each request:

```
Authorization: Bearer qk_xxxxxxxxxxxx.yyyyyyyy…
```

For example, to list customers: `curl -H "Authorization: Bearer <key>" https://your-server/api/resource/Customer`. The developer manual lists the API.

## Looking after your keys

- The list shows each key's description, whether it is read only, when it expires and when it was last used. Delete keys you no longer use: programs using a deleted key stop working at once.
- A key stops working when it expires, when you are disabled, or when it is deleted.
- You can have up to 10 keys. A key cannot make or delete keys: you must be signed in for that.
- System Managers can see and delete anyone's keys if one is lost or leaked.
