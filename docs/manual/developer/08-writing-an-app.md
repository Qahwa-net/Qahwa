# Writing an app

An **app** is a .NET class library with an `IQahwaApp` class. It can bring DocTypes (JSON files), hooks on documents, server functions, scheduled jobs, fixtures and patches. Several apps can be installed together, and on a server with several sites each site records the apps it has.

## The smallest app

```csharp
using Qahwa.Core;

public sealed class CrmApp : IQahwaApp
{
    public string Name => "crm";            // lower case; prefixes the app's methods
    public string Version => "1.2.0";
    public string Title => "Customer Relations";
    public string? DocTypesPath => Path.Combine(AppContext.BaseDirectory, "DocTypes");   // JSON files, searched recursively
    public IReadOnlyList<IPatch> Patches => [new FillTierPatch()];

    public void Configure(IAppBuilder app)
    {
        // Runs before a Deal is saved (also on submit, with BeforeSubmit). What it changes is checked like any other input.
        app.On("Deal", DocEvent.BeforeSave, ctx =>
        {
            if (Convert.ToDecimal(ctx.Doc["amount"]) < 0)
                ctx.Refuse("A deal cannot be negative.");              // stops the save with this message
            ctx.Doc["tier"] = Convert.ToDecimal(ctx.Doc["amount"]) >= 10_000 ? "Gold" : "Standard";
            return Task.CompletedTask;
        });

        // Runs once the Deal is stored. It acts as the person who saved: their permissions apply.
        app.On("Deal", DocEvent.AfterSubmit, async ctx =>
            await ctx.Documents.InsertAsync("ToDo", new Dictionary<string, object?>
            {
                ["allocated_to"] = ctx.User.User, ["reference_doctype"] = "Deal", ["reference_name"] = ctx.Name, ["description"] = "Send the contract",
            }, ctx.User, ctx.Cancellation));

        // POST /api/method/crm.deal_total   { "customer": "ACME" }
        app.Method("deal_total", async ctx =>
        {
            var page = await ctx.Documents.ListAsync("Deal", new ListRequest { Fields = ["amount"], Limit = 1000,
                Filters = [new RawFilter("customer", "=", ctx.RequiredText("customer"))] }, ctx.User, ctx.Cancellation);
            return page.Data.Sum(r => Convert.ToDecimal(r["amount"]));
        }, new MethodOptions { Roles = ["Sales Manager"], PerMinute = 30 });

        // About every hour. Must be safe to run twice (several servers may).
        app.Schedule("close_stale", TimeSpan.FromHours(1), async (services, ct) => { /* use services.GetRequiredService<...>() */ });

        // Made once, the first time the app is installed on a site.
        app.Fixture("Territory", new Dictionary<string, object?> { ["name"] = "Riyadh", ["territory_name"] = "Riyadh" });
    }
}
```

Register it in the host (`Program.cs`): `builder.Services.AddQahwaApp<CrmApp>();`. At the next start each site records it (**Installed App**), makes its fixtures once, runs its patches and starts its hooks, methods and jobs.

## Document hooks

`DocEvent`: `BeforeSave`, `BeforeSubmit`, `BeforeCancel`, `BeforeDelete`, `AfterSave`, `AfterSubmit`, `AfterCancel`, `AfterDelete`.

- **Before** hooks see the document being saved as `ctx.Doc`. Changes made in `BeforeSave` and `BeforeSubmit` are converted and validated like any other input (a wrong type is a validation error; system fields, tables and passwords cannot be set). `ctx.Refuse(text)` or a `DocumentValidationException` refuses the change. Any other exception stops the save too (a rule that cannot run cannot be trusted) and is written to the Error Log with the app's name.
- **After** hooks see what was stored. They cannot undo the save: an exception is written to the Error Log.
- A change a hook makes through `ctx.Documents` does not start other hooks (so they cannot loop), but it is validated, permission-checked and logged normally, as the person who made the original change.
- Hooks apply to DocTypes that are not part of the system (module Core) and are matched by exact DocType name; a hook for a DocType that does not exist stops the server at start-up with the app's name.

## Permission checks and queries ([ADR-074](../../adr/074-app-permission-hooks.md))

```csharp
app.HasPermission("Ticket", ctx => (string?)ctx.Doc.GetValueOrDefault("region") == RegionOf(ctx.User))
   .PermissionQuery("Ticket", user => [new PermissionFilter("region", [RegionOf(user)])]);
```

- `HasPermission` is asked for read, write, create, delete, submit and cancel (`ctx.Action`); `false` refuses. `PermissionQuery` adds "field is one of these values" conditions to every list, count, group-by and export, so refused documents are not even listed.
- They can **only take access away**; they run after roles, User Permissions and shares. A check that throws refuses, a query that throws (or names a bad column) lists nothing, and both are logged.
- They do not apply to Administrators or to the framework's own DocTypes, and a registration for a DocType that does not exist stops the server at start-up.

## Boot hooks ([ADR-084](../../adr/084-boot-hooks.md))

```csharp
app.Boot(async ctx => new { company = "Acme", canApprove = ctx.User.Roles.Contains("Approver") });
```

The answer (a small object, at most 16 KB) is sent to the Desk at start-up under the app's name: `GET /api/boot` returns `{ data: { apps: { acme: { … } } } }`, and the Desk reads it with `useAppBoot<T>("acme")`. It is built for each person who starts the Desk, so keep it fast; a hook that throws or answers too much is left out and written to the Error Log. One hook per app.

## Server functions (`/api/method`)

`POST /api/method/<app>.<name>` with a JSON object calls a registered function; the answer is `{ "data": … }`. `GET` works only for functions registered with `AllowGet`. Calls need a signed-in person (and one of `Roles`, when given; Administrators always pass), are limited per person and function (`PerMinute`, default 60), and the body may be at most 1 MB. Unknown or oddly written names are 404. `GET /api/method` lists the functions and the installed apps.

Built in (`qahwa.*`, permission-checked like the REST API): `get_count`, `get_value`, `get_list`, `set_value`, `rename_doc`.

```bash
curl -b jar.txt -X POST localhost:5101/api/method/qahwa.get_count -H "Content-Type: application/json" \
  -d '{ "doctype": "Customer", "filters": [["customer_type", "=", "Company"]] }'
```

## Patches, fixtures and jobs

- `Patches` are ordinary `IPatch` objects (see ADR-043): one-time, named with a date prefix, run once per site in name order.
- A `Fixture` with a `name` is skipped when that document already exists, and fixtures are made only the first time an app is installed on a site.
- A job runs at most about once per `every` (a minimum of one minute) on each server's scheduler tick; failures go to the Error Log and never stop other jobs.

## Developer mode

Set `Qahwa:Developer:Enabled` to `true` and `Qahwa:Developer:AppPath` to your app's DocType folder, and every DocType you create, change or delete in the Designer is also written to (or removed from) `<AppPath>/<name>.json`, whole files only, with the name reduced to letters, digits and underscores and the path kept inside the folder. Commit the files with the app. `POST /api/developer/export-all` (Administrator) writes everything designed so far. On start-up a DocType that exists both as a file and in the database is accepted quietly when the two are identical. Leave developer mode off on servers that people use.

## Testing an app

`TestEngine` (in `Qahwa.Testing`) has `AppCatalog`: `engine.AppCatalog.Add(new CrmApp())` activates the hooks and methods for the engine's `Documents` and `Methods`; `AppInstaller.InstallAllAsync()` records the app and makes fixtures; `AppJobs.RunDueAsync()` runs due jobs. See `tests/Qahwa.Core.Tests/AppTests.cs`.
