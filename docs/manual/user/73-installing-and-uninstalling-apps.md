# Installing and uninstalling apps

Apps that ship with your Qahwa server are installed on every site when the server starts. A System Manager can switch one off for this site in **Tools → Apps** (or with the command line below).

**Uninstall** stops the app's functions, scheduled jobs, reports, start-up data and rules, and the automatic actions it adds to documents. **Its documents and DocTypes stay**, so you can **Install** it again and lose nothing.

- An app that another app needs cannot be uninstalled until the other app is uninstalled. An app cannot be installed until the apps it needs are installed.
- A server restart does not bring an uninstalled app back.
- Other servers of the same site notice the change within a few minutes.

Command line (runs against the site's database; the app must have started at least once on the server):

```
qahwa list-apps     --site mysite --sites ./sites
qahwa uninstall-app shop --site mysite --sites ./sites
qahwa install-app   shop --site mysite --sites ./sites
```

Apps that need the one you switch off are switched off with it. Installing and uninstalling through the Desk is recorded in the Access Log.
