# Installing and uninstalling apps

An **app** is a package of extra features written by a developer: new kinds of documents, rules that run when documents are saved, your own API functions, scheduled jobs, reports and start-up data. The **Apps** screen (**Tools → Apps**, System Managers) lists the apps this server offers, with the title, the name, the version and whether each is **installed** on this site. Each has one button: **Uninstall** or **Install**.

**Where do apps come from?** Apps are not uploaded in the Desk. A developer builds an app into the server and restarts it; from then on it appears here and is installed on every site. If you want a new app, ask your developer, who can follow [Building and installing an app](../developer/11-building-and-installing-apps.md). If you only need your own DocTypes, **Export & Import** (Tools) moves them between sites without any code.

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
