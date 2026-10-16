# System Settings

**System Settings** (System Manager) holds choices for the whole site:

| Setting | Effect |
|---|---|
| Time zone | The zone dates and times are shown in (for example `Asia/Riyadh`) |
| Date format | `yyyy-mm-dd`, `dd/mm/yyyy` or `mm/dd/yyyy` in lists and forms |
| First day of the week | Where calendar weeks start |
| Default language code | The language people see until they pick their own in the top bar |

An invalid value (a time zone that does not exist) is ignored and the default is used.

## Restricting the networks that can use the system
Whoever deploys the system can limit it to certain networks with `Qahwa:Auth:AllowedIps` in the configuration:

```json
"Qahwa": { "Auth": { "AllowedIps": [ "203.0.113.5", "10.0.0.0/8" ] } }
```

From any other address the system answers "This system cannot be used from your network". Public web forms and the health check stay open. A wrong entry stops the server at start-up with a message, so mistakes are noticed at once. If you run behind a proxy, set up forwarded headers so the real address is checked. This is a configuration setting on purpose: it cannot lock administrators out from inside the Desk.
