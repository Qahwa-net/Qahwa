# Apps, server functions and developer mode

## Installed apps
Apps add DocTypes, rules and functions that were written by developers. A System Manager can see which are installed (and their versions) in the list **Installed App**. Apps are installed by the person who runs the server; the list is read-only.

## Server functions
Other systems (and scripts) can call functions on the server: `POST /api/method/<app>.<function>` with a JSON body. The built-in ones are `qahwa.get_count`, `qahwa.get_value`, `qahwa.get_list`, `qahwa.set_value` and `qahwa.rename_doc`; they do exactly what you could do yourself in the Desk, with your permissions. `GET /api/method` lists everything that is available. Calls need an API key or a signed-in session, may be limited to certain roles, and are limited per minute.

## Developer mode (for people who build apps)
If you are building an app, you can have the DocTypes you design in the Desk saved as files too:

```json
"Qahwa": { "Developer": { "Enabled": true, "AppPath": "C:/work/crm/DocTypes" } }
```

Every DocType you create, change or delete in **DocType Designer** is then written to (or removed from) `<AppPath>/<name>.json`; commit those files with your app. An Administrator can write everything designed so far with `POST /api/developer/export-all`. Keep developer mode off on servers that people use.
