# Building and installing an app: step by step

For a longer example built from an empty folder (a Project Management app, with its tests), read [Building an app from scratch](12-building-an-app-from-scratch.md).

This chapter takes you from nothing to an app that shows on the **Apps** screen of a running Qahwa. [Writing an app](08-writing-an-app.md) is the reference for everything an app can contain; this is the walk-through.

## What an app is, and what the Apps screen does

An **app** is a .NET class library that is **compiled into the server**. It is not uploaded through the Desk: Qahwa does not load code from a folder at run time. This is deliberate (an app runs with the server's rights, so it goes through your build and review like the rest of your code).

What the **Apps** screen (Desk, Tools, Apps; System Managers) shows is the apps the server has registered, with their version, and a switch **per site**:

| Button | What it does |
|---|---|
| **Uninstall** | Stops the app on this site: its hooks, server functions, scheduled jobs, script reports, start-up data and permission rules do nothing. Its DocTypes and documents stay |
| **Install** | Switches it on again. Nothing is lost |

So there are two different steps, and people often mix them up:

1. **Adding an app to the server** (a developer builds it into the server and starts the server). This is what the rest of this chapter describes.
2. **Installing it on a site** (the Apps screen, or `qahwa install-app`). A newly registered app is installed on every site automatically at the first start; the screen is for switching it off and on afterwards.

## What an app can bring

| Part | What it gives you | Example |
|---|---|---|
| **DocTypes** (JSON files) | New kinds of documents with tables, forms and lists, made when the server starts | `Acme Crm Item` |
| **Document hooks** | Code that runs before or after a save, submit, cancel or delete: validate, fill fields, refuse, or react | Refuse a negative amount; create a ToDo when a deal is submitted |
| **Server functions** | Your own API calls, `POST /api/method/<app>.<name>`, with roles and a rate limit | `acme_crm.item_count` |
| **Scheduled jobs** | Work that repeats (at most about once per interval) | Close stale deals every hour |
| **Fixtures** | Documents made once, the first time the app is installed on a site | Roles, territories |
| **Patches** | One-time data changes, run once per site, in order | Fill a new field for old documents |
| **Script reports** | Reports whose rows come from code, shown in **Script Reports** | Top customers |
| **Permission rules** | Extra checks that can only take access away | A person sees only their region |
| **Boot hook** | Data sent to the Desk when it starts | The company name |
| **Dependencies** | Other apps this one needs (installed first) | `billing` |

An app that is only DocTypes needs no code at all; see "A lighter way" at the end.

## Step by step

### 1. Make the app from the template

You need the .NET 10 SDK and a copy of the Qahwa repository. Put your app **inside the repository's `apps` folder**, so the same steps work on your machine and in the Docker build:

```bash
cd Qahwa                                    # the repository folder
dotnet new install ./templates/qahwa-app    # once
cd apps
dotnet new qahwa-app -n Acme.Crm --FrameworkPath ../../.. -o Acme.Crm
```

`--FrameworkPath` is the path **from the app project's folder to the Qahwa repository folder**; from `apps/Acme.Crm/Acme.Crm` that is `../../..`. You get:

```
apps/Acme.Crm/
  Acme.Crm/Acme.Crm.csproj          the library
  Acme.Crm/Acme.Crm.cs              the app (class QahwaAppModule)
  Acme.Crm/DocTypes/my_app_item.json   its DocType, "Acme Crm Item"
  Acme.Crm.Tests/                   tests that load the DocType and save a document
```

The app's name for URLs and methods is made from the project name (`acme_crm`); the DocType is `Acme Crm Item`.

### 2. Run its tests

```bash
dotnet test Acme.Crm/Acme.Crm.Tests
```

Two tests pass: the DocType loads and saving works, and the hook refuses a title containing "forbidden".

### 3. Make it your own

- **DocTypes:** edit the JSON or add files in `DocTypes/` (searched recursively; they are copied next to the assembly). Rename `my_app_item.json` and the DocType inside it, and add fields, permissions and naming. The [DocType reference](03-doctype-reference.md) lists every property. Faster: design the DocType in the Desk's **DocType Designer** with [developer mode](08-writing-an-app.md#developer-mode) on, and it writes the JSON file for you.
- **Behaviour:** edit `Configure` in `Acme.Crm.cs` (`app.On(...)` for hooks, `app.Method(...)`, `app.Schedule(...)`, `app.Fixture(...)`). The template's hook refuses a title containing "forbidden"; its method `item_count` counts the items.
- Change `Name` (lower case, it prefixes your methods), `Title` (shown on the Apps screen) and `Version`.

### 4. Add it to the server

Two edits.

**a)** In `src/Qahwa.Server/Qahwa.Server.csproj`, add a project reference:

```xml
<ProjectReference Include="..\..\apps\Acme.Crm\Acme.Crm\Acme.Crm.csproj" />
```

**b)** In `src/Qahwa.Server/SiteServices.cs`, register it beside the line for the CRM app (this is where every site's services are made, so the app is available on every site of a multi-site server):

```csharp
services.AddQahwaApp<Acme.Crm.QahwaAppModule>();
```

### 5. Start the server

```bash
docker compose up -d postgres
dotnet run --project src/Qahwa.Server        # Development: auto-migrates on start
```

At the start, each site records the app (**Installed App**), creates the app's tables, makes its fixtures once, runs its patches and starts its hooks, methods and jobs. In production set `Qahwa__AutoMigrate=true` or run `qahwa migrate` as a deployment step.

### 6. See it and try it

1. Open **Tools, Apps** in the Desk: *Acme Crm · acme_crm 0.1.0 · installed* appears.
2. Open **Acme Crm Item** from the sidebar (the module is made from the name) and make an item: it works. Try a title containing "forbidden": the hook refuses it.
3. Call the function:

```bash
curl -b jar.txt -X POST http://localhost:5101/api/method/acme_crm.item_count -H "Content-Type: application/json" -d '{}'
# {"data":{"count":1}}
```

   `GET /api/method` lists every function and every registered app with its version and whether it is installed.
4. Press **Uninstall** on the Apps screen: the function answers *There is no such method* and the hook stops; the items are still in the list. **Install** brings it back.

### 7. Run it in Docker

The [Dockerfile](../../../Dockerfile) builds the whole repository, so an app under `apps/` that the server references is built into the image. After the two edits of step 4:

```bash
docker compose up --build -d
```

Open http://localhost:8080, **Tools, Apps** shows it. (An app outside the repository folder is not part of the Docker build context: keep it under `apps/`, or in your own fork of the repository.)

### 8. Release a new version

1. Raise `Version` in the app class.
2. Add a **patch** for any data change (an `IPatch` named with a date prefix, listed in `Patches`); it runs once per site at the next start, after the patches of the apps this one depends on.
3. Build, deploy and restart. `AutoMigrate` adds new tables and columns (it never drops data). The Apps screen shows the new version.

### 9. Remove it

- **From one site:** Uninstall on the Apps screen (data stays).
- **From the server:** remove the registration line and the project reference, then rebuild. The data stays in the database; delete the DocTypes' tables yourself only if you are sure.

## Several sites and several servers

Every site has its own record of what is installed, so you can switch an app off for one site and keep it on for another. Other servers of the same site notice a change within a few minutes. From the command line: `qahwa list-apps`, `qahwa install-app <app>`, `qahwa uninstall-app <app>` with `--site <name> --sites <dir>` (or `--connection`).

## A lighter way: no code

If all you need is DocTypes, workflows, print formats and settings (no hooks or functions), you do not need an app:

- **Export & Import** (Tools) writes DocTypes and their settings to one file and loads it on another site (see the bundle chapter in the user guide), or
- keep the DocType JSON files in a folder and list it in `Qahwa:DocTypePaths` (in Docker: `Qahwa__DocTypePaths__1=/app/mydoctypes`, with the folder mounted into the container).

## Checklist when it does not show up

| Check | Why |
|---|---|
| The project reference and `AddQahwaApp` are both there | The Apps screen lists only registered apps |
| The server was rebuilt and restarted (or `docker compose up --build`) | Apps are compiled in |
| You are a System Manager | The screen needs that role |
| The log has no `Error Log` entry naming the app | A hook for a DocType that does not exist, a missing dependency or a circle stops the server at start with the app's name |
| The DocType JSON is copied (`Content Include="DocTypes\**\*.json"`) | The server finds the files next to the assembly |
