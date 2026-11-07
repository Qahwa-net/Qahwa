using System.Globalization;
using System.Text.Json;
using Qahwa.Core;

namespace Qahwa.ProjectManagement;

/// <summary>
/// Project Management: projects, their tasks and the time people log on them. It is the worked example of the developer guide
/// (docs/manual/developer/12-building-an-app-from-scratch.md): three DocTypes written as JSON files, hooks that keep the numbers
/// right, a server function, a script report and the roles the permission rules name.
/// Everything else (assignments, comments and mentions, reminders, tags, the board view, dashboards) is already in the framework.
/// </summary>
public sealed class ProjectManagementApp : IQahwaApp
{
    public const string ProjectDoc = "Pm Project";
    public const string TaskDoc = "Pm Task";
    public const string TimeLogDoc = "Pm Time Log";

    public string Name => "project_management";

    public string Version => "0.1.0";

    public string Title => "Project Management";

    public string? DocTypesPath => Path.Combine(AppContext.BaseDirectory, "DocTypes");

    public void Configure(IAppBuilder app)
    {
        // The roles the DocTypes' permission rules name; made once, the first time the app is installed on a site.
        app.Fixture("Role", new Dictionary<string, object?> { ["role_name"] = "Project User" });
        app.Fixture("Role", new Dictionary<string, object?> { ["role_name"] = "Project Manager" });

        // A task: it needs an open project, and it remembers the day it was finished.
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

        // A task changed: the project's progress follows (and the old project's, when a task moves to another one).
        app.On(TaskDoc, DocEvent.AfterSave, async ctx =>
        {
            await RefreshProjectAsync(ctx, ctx.Doc.GetValueOrDefault("project") as string);
            if (ctx.Old?.GetValueOrDefault("project") as string is { } before && before != ctx.Doc.GetValueOrDefault("project") as string)
                await RefreshProjectAsync(ctx, before);
        });
        app.On(TaskDoc, DocEvent.AfterDelete, ctx => RefreshProjectAsync(ctx, ctx.Doc.GetValueOrDefault("project") as string));

        // A time log: whose it is, and the task's and project's hours follow it.
        app.On(TimeLogDoc, DocEvent.BeforeSave, ctx =>
        {
            if (string.IsNullOrEmpty(ctx.Doc.GetValueOrDefault("user") as string))
                ctx.Doc["user"] = ctx.User.User;
            return Task.CompletedTask;
        });
        app.On(TimeLogDoc, DocEvent.AfterSave, async ctx =>
        {
            await RefreshTaskHoursAsync(ctx, ctx.Doc.GetValueOrDefault("task") as string);
            if (ctx.Old?.GetValueOrDefault("task") as string is { } before && before != ctx.Doc.GetValueOrDefault("task") as string)
                await RefreshTaskHoursAsync(ctx, before);
        });
        app.On(TimeLogDoc, DocEvent.AfterDelete, ctx => RefreshTaskHoursAsync(ctx, ctx.Doc.GetValueOrDefault("task") as string));

        // POST /api/method/project_management.summary  { "project": "PRJ-00001" }
        app.Method("summary", async ctx =>
        {
            var projectName = ctx.RequiredText("project");
            var project = await ctx.Documents.GetAsync(ProjectDoc, projectName, ctx.User, ctx.Cancellation);
            var tasks = await TasksOfAsync(ctx.Documents, ctx.User, projectName, ctx.Cancellation);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var byStatus = tasks.GroupBy(t => (string)t["status"]!).ToDictionary(g => g.Key, g => g.Count());
            return new
            {
                project = projectName,
                status = project["status"],
                tasks = tasks.Count,
                byStatus,
                overdue = tasks.Count(t => IsOverdue(t, today)),
                percentComplete = Percent(tasks),
                loggedHours = project["logged_hours"] ?? 0d,
                budgetHours = project["budget_hours"],
            };
        }, new MethodOptions { PerMinute = 60 });

        // One row per project: where it stands, how far along, and the hours used against the budget.
        app.ScriptReport("project_status", "Project status", async ctx =>
        {
            var projects = (await ctx.Documents.ListAsync(ProjectDoc, new ListRequest
            {
                Fields = ["name", "project_name", "status", "manager", "end_date", "budget_hours", "logged_hours", "percent_complete"], Limit = 5_000,
            }, ctx.User, ctx.Cancellation)).Data;
            var tasks = (await ctx.Documents.ListAsync(TaskDoc, new ListRequest
            {
                Fields = ["project", "status", "due_date"], Limit = 20_000,
            }, ctx.User, ctx.Cancellation)).Data;
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var rows = projects.Select(p =>
            {
                var mine = tasks.Where(t => t["project"] as string == (string)p["name"]!).ToList();
                return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["project"] = p["name"], ["title"] = p["project_name"], ["status"] = p["status"], ["manager"] = p["manager"], ["end_date"] = p["end_date"],
                    ["tasks"] = (long)mine.Count, ["overdue"] = (long)mine.Count(t => IsOverdue(t, today)),
                    ["percent"] = p["percent_complete"] ?? 0m, ["budget_hours"] = p["budget_hours"], ["logged_hours"] = p["logged_hours"] ?? 0d,
                };
            }).ToList();
            return new ScriptReportData(
                [new("project", "Project"), new("title", "Name"), new("status", "Status"), new("manager", "Manager"), new("end_date", "End date", "Date"),
                 new("tasks", "Tasks", "Int"), new("overdue", "Overdue", "Int"), new("percent", "Complete (%)", "Percent"),
                 new("budget_hours", "Budget (h)", "Float"), new("logged_hours", "Logged (h)", "Float")],
                rows);
        }, new ScriptReportOptions { Roles = ["Project Manager", "Project User"] });
    }

    private static bool IsOverdue(IReadOnlyDictionary<string, object?> task, DateOnly today) =>
        task["status"] as string != "Done" && task.GetValueOrDefault("due_date") is { } due && ToDate(due) is { } day && day < today;

    private static DateOnly? ToDate(object value) => value switch
    {
        DateOnly d => d,
        DateTime t => DateOnly.FromDateTime(t),
        string s when DateOnly.TryParse(s, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    private static decimal Percent(IReadOnlyList<Dictionary<string, object?>> tasks) =>
        tasks.Count == 0 ? 0m : Math.Round(100m * tasks.Count(t => t["status"] as string == "Done") / tasks.Count, 0);

    private static async Task<List<Dictionary<string, object?>>> TasksOfAsync(DocumentService documents, IUserContext user, string project, CancellationToken ct) =>
        (await documents.ListAsync(TaskDoc, new ListRequest
        {
            Fields = ["name", "status", "due_date", "logged_hours"], Limit = 10_000, Filters = [new RawFilter("project", "=", project)],
        }, user, ct)).Data.ToList();

    private static double Hours(IEnumerable<Dictionary<string, object?>> rows, string field) =>
        Math.Round(rows.Sum(r => r.GetValueOrDefault(field) is { } v ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0d), 2);

    /// <summary>Works out a project's progress and hours from its tasks and stores them on the project.</summary>
    private static async Task RefreshProjectAsync(DocHookContext ctx, string? project)
    {
        if (string.IsNullOrEmpty(project))
            return;
        var tasks = await TasksOfAsync(ctx.Documents, ctx.User, project, ctx.Cancellation);
        await ctx.Documents.UpdateAsync(ProjectDoc, project,
            new Dictionary<string, object?> { ["percent_complete"] = Percent(tasks), ["logged_hours"] = Hours(tasks, "logged_hours") }, ctx.User, ctx.Cancellation);
    }

    /// <summary>Adds up the time logged on a task, stores it on the task, and then refreshes the task's project.</summary>
    private static async Task RefreshTaskHoursAsync(DocHookContext ctx, string? task)
    {
        if (string.IsNullOrEmpty(task))
            return;
        var logs = (await ctx.Documents.ListAsync(TimeLogDoc, new ListRequest
        {
            Fields = ["hours"], Limit = 10_000, Filters = [new RawFilter("task", "=", task)],
        }, ctx.User, ctx.Cancellation)).Data;
        var updated = await ctx.Documents.UpdateAsync(TaskDoc, task, new Dictionary<string, object?> { ["logged_hours"] = Hours(logs, "hours") }, ctx.User, ctx.Cancellation);
        await RefreshProjectAsync(ctx, updated.GetValueOrDefault("project") as string);
    }
}
