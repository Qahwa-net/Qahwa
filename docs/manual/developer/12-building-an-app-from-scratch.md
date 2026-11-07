# Building an app from scratch: Project Management

This chapter builds a complete app, **Project Management**, from an empty folder, explaining each decision. The finished code is in the repository (`apps/Qahwa.ProjectManagement` and its tests); read it beside this chapter. For the shorter "scaffold from the template and install it" route, see [Building and installing an app](11-building-and-installing-apps.md); for every feature an app can use, [Writing an app](08-writing-an-app.md).

## 1. What we are building

A small tracker for projects, their tasks and the time people spend:

| DocType | What it is | Rules the app enforces |
|---|---|---|
| **Pm Project** | A piece of work with a manager, dates, an hours budget and a status | End date is not before the start date. Its **percent complete** and **logged hours** are worked out from its tasks and cannot be typed |
| **Pm Task** | One thing to do in a project, with a status, a priority, a person and a due date | A task cannot be added to a completed or cancelled project. Finishing a task stamps the day; reopening clears it |
| **Pm Time Log** | Hours a person spent on a task | Hours are more than 0 and at most 24. The log belongs to the person who made it. The task's and project's hours follow it |

It also brings a **server function** (`project_management.summary`: tasks by status, how many are overdue, progress and hours), a **script report** (*Project status*, a row per project) and two **roles** (*Project User* works on tasks and logs time; *Project Manager* also creates and deletes projects). Boards, assignments, comments, reminders, tags, print formats and dashboards come with the framework, so the app does not build them.

**How to decide what goes where.** Use this order of preference, from least code to most:

1. **A DocType property** (required, default, `dependsOn`, `validations`, `formula`, `fetchFrom`, permissions): no code, shown in the form, checked on the server.
2. **A hook** for a rule that needs other documents or a decision (refuse, roll up, stamp).
3. **A server function** for a question or an action that is not "save this document".
4. **A script report** for a table that is computed.

## 2. Create the project

```bash
cd Qahwa/apps                                   # inside the repository, so Docker builds it too
dotnet new classlib -n Qahwa.ProjectManagement -f net10.0
dotnet new xunit    -n Qahwa.ProjectManagement.Tests -f net10.0
rm Qahwa.ProjectManagement/Class1.cs
cd ..
dotnet sln add apps/Qahwa.ProjectManagement apps/Qahwa.ProjectManagement.Tests
```

(`dotnet new qahwa-app` makes all of this, including a sample DocType and tests; here we do it by hand to see every part.) The library's project file references the framework core and ships the DocType files next to the assembly:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Qahwa.Core\Qahwa.Core.csproj" />
  </ItemGroup>
  <!-- The DocType files travel with the app: the host finds them next to the assembly. -->
  <ItemGroup>
    <Content Include="DocTypes\**\*.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

The tests project references the library and `tests/Qahwa.Testing` (the in-memory engine) and uses xUnit; copy `apps/Qahwa.Crm.Tests/Qahwa.Crm.Tests.csproj` as a starting point.

## 3. Write the DocTypes

Make `apps/Qahwa.ProjectManagement/DocTypes/` and add one JSON file per DocType. The top line `"$schema": "../../../docs/schema/doctype.schema.json"` gives your editor completion and checking. Start with the project:

```json
{
  "$schema": "../../../docs/schema/doctype.schema.json",
  "name": "Pm Project",
  "module": "Project Management",
  "autoname": "PRJ-.#####",
  "titleField": "project_name",
  "defaultView": "board",
  "searchFields": ["project_name", "manager"],
  "fields": [
    { "name": "project_name", "type": "Data", "label": "Project", "required": true, "inListView": true },
    { "name": "status", "type": "Select", "label": "Status", "options": "Planning\nActive\nOn Hold\nCompleted\nCancelled", "default": "Planning", "inListView": true, "inStandardFilter": true },
    { "name": "manager", "type": "Link", "options": "User", "label": "Project manager", "inListView": true, "inStandardFilter": true },
    { "name": "start_date", "type": "Date", "label": "Start date" },
    { "name": "end_date", "type": "Date", "label": "End date", "inListView": true },
    { "name": "budget_hours", "type": "Float", "label": "Budget (hours)", "nonNegative": true },
    { "name": "logged_hours", "type": "Float", "label": "Logged (hours)", "readOnly": true, "noCopy": true },
    { "name": "percent_complete", "type": "Percent", "label": "Complete (%)", "precision": 0, "readOnly": true, "noCopy": true, "inListView": true },
    { "name": "description", "type": "TextEditor", "label": "Description" }
  ],
  "statusField": "status",
  "indicators": [
    { "value": "Planning", "color": "blue" }, { "value": "Active", "color": "green" }, { "value": "On Hold", "color": "orange" },
    { "value": "Completed", "color": "purple" }, { "value": "Cancelled", "color": "gray" }
  ],
  "validations": [
    { "expression": "end_date == null || start_date == null || end_date >= start_date", "message": "The end date cannot be before the start date.", "field": "end_date" }
  ],
  "permissions": [
    { "role": "Project User", "read": true, "write": true, "export": true },
    { "role": "Project Manager", "read": true, "write": true, "create": true, "delete": true, "export": true, "report": true }
  ]
}
```

What each choice does:

- **`name`**: the DocType's name. Give app DocTypes a short prefix (`Pm …`) so they cannot clash with another app's. The **`module`** groups them in the sidebar.
- **`autoname`** `PRJ-.#####` gives names `PRJ-00001`. `titleField` is what lists and links show.
- **`defaultView: "board"`** opens the list as a board with a column per `statusField` value; `indicators` colour the status.
- **`readOnly`** on `logged_hours` and `percent_complete` makes them unwritable in the form; the **hook** fills them. `noCopy` keeps them out of Duplicate.
- **`validations`** is a rule in the [expression language](../../expressions.md); it runs in the form and again on the server. This is a rule you do not need code for.
- **`permissions`** names roles. A role must exist: the app makes them in a fixture (section 4). *Project User* can read and write projects (so the roll-up hook, which runs **as the person who saved a task**, may update the project) but not create or delete them.

The other two files follow the same pattern; the points worth copying:

- **Pm Task** has `"project": { "type": "Link", "options": "Pm Project", "required": true }`, a `status` Select (*Open, In Progress, Review, Done*) that drives its board, `priority`, `assigned_to` (a Link to `User`), `due_date`, `estimated_hours`, and the read-only `logged_hours` and `completed_on` (shown only `"dependsOn": "status == 'Done'"`).
- **Pm Time Log** fills its project automatically with `"fetchFrom": "task.project"`, defaults the date with `"default": "=today()"`, and has `"validations": [{ "expression": "hours > 0 && hours <= 24", "message": "Hours are more than 0 and at most 24.", "field": "hours" }]`.

You do not have to write the JSON by hand: turn on [developer mode](08-writing-an-app.md#developer-mode) (`Qahwa:Developer:Enabled`, `AppPath` = your `DocTypes` folder), design the DocType in the Desk's **DocType Designer**, and the file is written for you.

## 4. The app class

```csharp
public sealed class ProjectManagementApp : IQahwaApp
{
    public const string ProjectDoc = "Pm Project";
    public const string TaskDoc = "Pm Task";
    public const string TimeLogDoc = "Pm Time Log";

    public string Name => "project_management";       // lower case; prefixes the app's methods and ids
    public string Version => "0.1.0";
    public string Title => "Project Management";       // shown on the Apps screen
    public string? DocTypesPath => Path.Combine(AppContext.BaseDirectory, "DocTypes");

    public void Configure(IAppBuilder app)
    {
        // Made once, the first time the app is installed on a site.
        app.Fixture("Role", new Dictionary<string, object?> { ["role_name"] = "Project User" });
        app.Fixture("Role", new Dictionary<string, object?> { ["role_name"] = "Project Manager" });
        // ... hooks, function and report follow
    }
}
```

`Configure` runs once at start and **declares** what the app adds; it must not do work itself. Keep the DocType names in constants so a typo is a compile error. (Do not call a constant `Task`: it hides `System.Threading.Tasks.Task` inside the class and the async methods stop compiling.)

## 5. Hooks: rules that need code

A hook is `app.On(<DocType>, <DocEvent>, async ctx => …)`. Before-hooks may change `ctx.Doc` or refuse; after-hooks see what was stored.

**A task: an open project, and a completion stamp** (before save):

```csharp
app.On(TaskDoc, DocEvent.BeforeSave, async ctx =>
{
    var projectName = ctx.Doc.GetValueOrDefault("project") as string;
    var projectChanged = ctx.Old?.GetValueOrDefault("project") as string != projectName;
    if (projectName is not null && projectChanged)
    {
        var project = await ctx.Documents.GetAsync(ProjectDoc, projectName, ctx.User, ctx.Cancellation);
        if (project["status"] as string is "Completed" or "Cancelled")
            ctx.Refuse($"{projectName} is {((string)project["status"]!).ToLowerInvariant()}: tasks cannot be added to it.");
    }

    var status = ctx.Doc.GetValueOrDefault("status") as string;
    var wasDone = ctx.Old?.GetValueOrDefault("status") as string == "Done";
    if (status == "Done" && !wasDone)
        ctx.Doc["completed_on"] = DateOnly.FromDateTime(DateTime.UtcNow);
    else if (status != "Done")
        ctx.Doc["completed_on"] = null;
});
```

- `ctx.Old` is the stored version (`null` for a new document), so "changed" is `Old != new`. The check runs only when the project is new or changed, so editing an old task of a project that has since closed still works.
- `ctx.Refuse(text)` stops the save with that message. Changes made to `ctx.Doc` are validated like any other input.

**A roll-up: when tasks change, the project follows** (after save and delete):

```csharp
app.On(TaskDoc, DocEvent.AfterSave, async ctx =>
{
    await RefreshProjectAsync(ctx, ctx.Doc.GetValueOrDefault("project") as string);
    if (ctx.Old?.GetValueOrDefault("project") as string is { } before && before != ctx.Doc.GetValueOrDefault("project") as string)
        await RefreshProjectAsync(ctx, before);      // a task moved: the old project changes too
});
app.On(TaskDoc, DocEvent.AfterDelete, ctx => RefreshProjectAsync(ctx, ctx.Doc.GetValueOrDefault("project") as string));
```

```csharp
private static async Task RefreshProjectAsync(DocHookContext ctx, string? project)
{
    if (string.IsNullOrEmpty(project)) return;
    var tasks = await TasksOfAsync(ctx.Documents, ctx.User, project, ctx.Cancellation);
    await ctx.Documents.UpdateAsync(ProjectDoc, project,
        new Dictionary<string, object?> { ["percent_complete"] = Percent(tasks), ["logged_hours"] = Hours(tasks, "logged_hours") },
        ctx.User, ctx.Cancellation);
}
```

Three facts about hooks make this safe:

1. **Hooks act as the person who saved.** `ctx.Documents` applies that person's permissions, which is why *Project User* has write on `Pm Project`. A hook is never a back door.
2. **A change a hook makes does not start other hooks**, so the task hook updating the project (and the log hook updating the task) cannot loop. The cost: when the *log* hook updates a task, the *task* hook does not run, so the log hook calls `RefreshProjectAsync` itself.
3. **A failing after-hook cannot undo the save.** The error goes to the **Error Log** with the app's name. A failing before-hook stops the save.

**A time log: ownership and hours** (the same pattern: `RefreshTaskHoursAsync` sums the task's logs, updates the task, then refreshes the project). In `BeforeSave`, fill the person if empty: `ctx.Doc["user"] = ctx.User.User`.

## 6. A server function

```csharp
// POST /api/method/project_management.summary   { "project": "PRJ-00001" }
app.Method("summary", async ctx =>
{
    var projectName = ctx.RequiredText("project");
    var project = await ctx.Documents.GetAsync(ProjectDoc, projectName, ctx.User, ctx.Cancellation);
    var tasks = await TasksOfAsync(ctx.Documents, ctx.User, projectName, ctx.Cancellation);
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    return new
    {
        project = projectName, status = project["status"], tasks = tasks.Count,
        byStatus = tasks.GroupBy(t => (string)t["status"]!).ToDictionary(g => g.Key, g => g.Count()),
        overdue = tasks.Count(t => IsOverdue(t, today)),
        percentComplete = Percent(tasks), loggedHours = project["logged_hours"] ?? 0d, budgetHours = project["budget_hours"],
    };
}, new MethodOptions { PerMinute = 60 });
```

The id is `<app name>.<function>`. `GetAsync` throws not-found or permission-denied for a project the caller may not read, and the API turns that into 404/403: the function needs no checks of its own. `MethodOptions` can require roles (`Roles = ["Project Manager"]`) and sets the rate limit. Try it:

```bash
curl -b jar.txt -X POST http://localhost:5101/api/method/project_management.summary \
  -H "Content-Type: application/json" -d '{"project":"PRJ-00001"}'
```

## 7. A script report

`app.ScriptReport("project_status", "Project status", async ctx => …, new ScriptReportOptions { Roles = [...] })` returns `ScriptReportData(columns, rows)`. Read through `ctx.Documents` as `ctx.User`, so each person's report shows only what they may see. Columns are `new("key", "Label", "Type")` (types such as `Int`, `Float`, `Percent`, `Date`); the report appears as `project_management.project_status` under **Script Reports**.

## 8. Test it

The in-memory `TestEngine` runs your DocTypes, hooks and functions with no database:

```csharp
public class ProjectManagementAppTests : IAsyncLifetime
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Project User", UserContext.AllRole]);
    private static readonly UserContext Boss = new("boss@example.com", ["Project Manager", UserContext.AllRole]);

    public ProjectManagementAppTests()
    {
        var app = new ProjectManagementApp();
        foreach (var file in Directory.GetFiles(app.DocTypesPath!, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            _engine.Registry.Add(DocTypeLoader.Parse(File.ReadAllText(file)));
        _engine.Registry.ValidateReferences();
        _engine.AppCatalog.Add(app);
    }

    // Link fields to User are checked, so the people must exist, as on a real site.
    public async Task InitializeAsync()
    {
        foreach (var person in new[] { Ann, Boss })
            await _engine.Documents.InsertAsync("User", Values(("email", person.User), ("full_name", person.User)), TestUsers.Admin);
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Finishing_tasks_moves_the_projects_percent_complete()
    {
        var project = await NewProjectAsync();
        var first = await NewTaskAsync(project, "One");
        await NewTaskAsync(project, "Two");
        await _engine.Documents.UpdateAsync(ProjectManagementApp.TaskDoc, first, Values(("status", "Done")), Ann);
        Assert.Equal(50m, Convert.ToDecimal((await ProjectAsync(project))["percent_complete"]));
    }
}
```

The 13 tests in the repository cover: names and loading, dates, the roll-up and its deletion, the completion stamp, closed projects, hours adding up and belonging to the person, the 0–24 rule (three values), permissions, the function and the report. Run them with `dotnet test apps/Qahwa.ProjectManagement.Tests`. Test as **different users**: most mistakes in an app are permission mistakes (a role missing write on something a hook updates).

## 9. Register it and install it

Add the project to the server and register it. In `src/Qahwa.Server/Qahwa.Server.csproj`:

```xml
<ProjectReference Include="..\..\apps\Qahwa.ProjectManagement\Qahwa.ProjectManagement.csproj" />
```

In `src/Qahwa.Server/SiteServices.cs`, beside the CRM app. Apps that ship with Qahwa are **off until a site's settings turn them on**, so an upgrade never adds DocTypes or roles to a site that did not ask:

```csharp
if (config.GetValue<bool>("Qahwa:Apps:ProjectManagement"))
    services.AddQahwaApp<Qahwa.ProjectManagement.ProjectManagementApp>();
```

(For an app that belongs to every site, drop the `if`.) Then switch it on for the site and start:

| Where | Setting |
|---|---|
| `dotnet run` | already on: `Properties/launchSettings.json` sets `Qahwa__Apps__ProjectManagement=true` (if you start the server another way, set that variable yourself) |
| Docker | already on in `docker-compose.yml` (`QAHWA_APP_PROJECT_MANAGEMENT`, default `true`; set it to `false` in `.env` to turn it off) |
| Any other site | `Qahwa__Apps__ProjectManagement=true` (or `Qahwa:Apps:ProjectManagement` in the site's settings), then restart |

```bash
docker compose up -d postgres
dotnet run --project src/Qahwa.Server          # or: docker compose up --build
```

At the start the site records the app (**Installed App**), creates the three tables, makes the two roles once, and starts the hooks, function and report. Check it:

1. **Tools → Apps** lists *Project Management · project_management 0.1.0 · installed*.
2. Give a person the **Project User** or **Project Manager** role in their User document.
3. The **Project Management** module appears in the sidebar: *Pm Project*, *Pm Task*, *Pm Time Log*. Make a project, add tasks (the board has a column per status; drag a card to change its status), log time, and watch the project's percent and hours change.
4. **Script Reports → Project status** shows the table; `GET /api/method` lists `project_management.summary`.

## 10. Make it better: where to go next

| Idea | How |
|---|---|
| Each person sees only the projects they belong to | `app.HasPermission(...)` and `app.PermissionQuery(...)` ([ADR-074](../../adr/074-app-permission-hooks.md)), for example filter tasks by `assigned_to` or project membership. They can only take access away |
| Remind people of overdue tasks | `app.Schedule("overdue_reminders", TimeSpan.FromHours(6), async (services, ct) => …)`; it must be safe to run twice |
| Show the number of my open tasks in the Desk header | `app.Boot(...)` |
| Small rules without a build (a tasks box, a due-date email, a form hint) | [Server and client scripts](13-server-and-client-scripts.md) use this app as their example |
| Fill a new field for existing tasks after an update | An `IPatch` in `Patches`, named with a date prefix; it runs once per site |
| Use another app's DocTypes | `Dependencies => ["other_app"]` |
| A Done column that needs approval | A [workflow](06-features-reference.md) on `Pm Task`, no code |
| A button that calls your function | A Document Action on the DocType |
| Typed field names in C# | The source generator ([Writing an app](08-writing-an-app.md#typed-documents)) |

## 11. Pitfalls we met while building it

| Problem | Cause and fix |
|---|---|
| `Task<…>` stops compiling inside the app class | A constant called `Task` hides the .NET type: name the DocType constants `TaskDoc` |
| A save fails with *User 'ann@example.com' does not exist* in a test | Link fields are validated: insert the `User` documents first |
| A roll-up hook fails with *permission denied* for a normal user | Hooks act as the saver: the role needs write on the document the hook updates |
| Totals are wrong for some users | The hook reads documents as the saver; a rule such as `ifOwner` on the DocType you add up hides other people's rows. Do not restrict the DocTypes you roll up |
| A hook's change did not trigger another hook | By design (no loops): call the second step yourself, as the log hook calls the project refresh |
| The app is not on the Apps screen | It is not registered, not switched on for the site (`Qahwa:Apps:…`), or the server was not rebuilt and restarted |
