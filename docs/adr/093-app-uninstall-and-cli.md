# ADR-093: Uninstalling and installing apps per site, and from the command line

Status: accepted

## Context
Every registered app was installed on every site at start-up and could not be switched off. A site that does not want an app's hooks, scheduled jobs or reports had to remove the app from the server entirely.

## Decision
- **A status on the site's Installed App record** (`Installed`, the default, or `Uninstalled`). **Uninstalling switches the app off for that site; nothing is deleted.** Its DocTypes, documents and fixtures stay (so installing again loses nothing); its document hooks, server functions, scheduled jobs, script reports, boot data and permission rules stop (`AppCatalog` filters them by the site's disabled set, replaced as a whole so readers never see it half-changed).
- **Rules** (`AppInstaller`): an app that another installed app needs cannot be uninstalled (the message names the apps); an app cannot be installed unless the apps it needs are installed; repeating an action is harmless; an unknown app is a 404. At start-up apps recorded as uninstalled stay off, and apps seen for the first time install as before. An app whose dependency is off is off too, however many steps away, so a change that skipped the rules (the command line) cannot leave a working app on a missing base.
- **Where to do it:** the Desk's **Apps** page (Tools; System Manager) and `POST /api/apps/{name}/install|uninstall` (System Manager or Administrator; written to the Access Log as *Install App* / *Uninstall App*); and the command line: `qahwa list-apps`, `qahwa install-app <app>` and `qahwa uninstall-app <app>` with `--connection` or `--site` and `--sites`. The command line only changes the record (it does not know the apps' code), so the app must have been recorded by a server start first.
- **Other servers and the command line:** the scheduler re-reads the records on every tick (`installed apps` step, logged when the set changes), so a change made elsewhere takes effect within a few minutes (the scheduler interval, five minutes by default).

## Consequences
- Uninstall is "switch off", not "remove": to delete an app's data, delete the documents and DocTypes by hand, or remove the app's code from the server.
- Version ranges and removing an app's code are separate matters.
