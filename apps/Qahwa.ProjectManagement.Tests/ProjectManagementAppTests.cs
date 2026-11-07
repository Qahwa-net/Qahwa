using System.Text.Json;
using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.ProjectManagement.Tests;

public class ProjectManagementAppTests : IAsyncLifetime
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Project User", UserContext.AllRole]);
    private static readonly UserContext Boss = new("boss@example.com", ["Project Manager", UserContext.AllRole]);
    private static readonly UserContext Outsider = new("eve@example.com", ["Other", UserContext.AllRole]);

    public ProjectManagementAppTests()
    {
        var app = new ProjectManagementApp();
        foreach (var file in Directory.GetFiles(app.DocTypesPath!, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            _engine.Registry.Add(DocTypeLoader.Parse(File.ReadAllText(file)));
        _engine.Registry.ValidateReferences();
        _engine.AppCatalog.Add(app);
    }

    /// <summary>The people named in Link fields must exist, as they do on a real site.</summary>
    public async Task InitializeAsync()
    {
        foreach (var person in new[] { Ann, Boss })
            await _engine.Documents.InsertAsync("User", Values(("email", person.User), ("full_name", person.User)), TestUsers.Admin);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private async Task<string> NewProjectAsync(string name = "Website", params (string Key, object? Value)[] more) =>
        (string)(await _engine.Documents.InsertAsync(ProjectManagementApp.ProjectDoc, Values([("project_name", name), .. more]), Boss))["name"]!;

    private async Task<string> NewTaskAsync(string project, string subject = "Design", params (string Key, object? Value)[] more) =>
        (string)(await _engine.Documents.InsertAsync(ProjectManagementApp.TaskDoc, Values([("subject", subject), ("project", project), .. more]), Ann))["name"]!;

    private Task<Dictionary<string, object?>> ProjectAsync(string name) => _engine.Documents.GetAsync(ProjectManagementApp.ProjectDoc, name, Boss);

    private Task<Dictionary<string, object?>> TaskAsync(string name) => _engine.Documents.GetAsync(ProjectManagementApp.TaskDoc, name, Ann);

    [Fact]
    public void The_app_has_a_valid_name_and_loads_its_three_doctypes()
    {
        Assert.Matches("^[a-z][a-z0-9_]{1,30}$", new ProjectManagementApp().Name);
        foreach (var doctype in new[] { ProjectManagementApp.ProjectDoc, ProjectManagementApp.TaskDoc, ProjectManagementApp.TimeLogDoc })
            Assert.True(_engine.Registry.TryGet(doctype, out _), doctype);
    }

    [Fact]
    public async Task A_project_starts_in_planning_and_its_dates_must_make_sense()
    {
        var project = await ProjectAsync(await NewProjectAsync());
        Assert.Equal("Planning", project["status"]);
        Assert.StartsWith("PRJ-", (string)project["name"]!);

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => NewProjectAsync("Backwards", ("start_date", "2026-05-01"), ("end_date", "2026-04-01")));
        Assert.Contains(ex.Errors, e => e.Field == "end_date");
    }

    [Fact]
    public async Task Finishing_tasks_moves_the_projects_percent_complete()
    {
        var project = await NewProjectAsync();
        var first = await NewTaskAsync(project, "One");
        await NewTaskAsync(project, "Two");
        Assert.Equal(0m, Convert.ToDecimal((await ProjectAsync(project))["percent_complete"]));

        await _engine.Documents.UpdateAsync(ProjectManagementApp.TaskDoc, first, Values(("status", "Done")), Ann);
        Assert.Equal(50m, Convert.ToDecimal((await ProjectAsync(project))["percent_complete"]));
    }

    [Fact]
    public async Task A_done_task_remembers_the_day_and_forgets_it_when_reopened()
    {
        var task = await NewTaskAsync(await NewProjectAsync());
        await _engine.Documents.UpdateAsync(ProjectManagementApp.TaskDoc, task, Values(("status", "Done")), Ann);
        Assert.NotNull((await TaskAsync(task))["completed_on"]);

        await _engine.Documents.UpdateAsync(ProjectManagementApp.TaskDoc, task, Values(("status", "In Progress")), Ann);
        Assert.Null((await TaskAsync(task))["completed_on"]);
    }

    [Fact]
    public async Task Deleting_a_task_updates_the_project()
    {
        var project = await NewProjectAsync();
        var done = await NewTaskAsync(project, "Done one", ("status", "Done"));
        var open = await NewTaskAsync(project, "Open one");
        Assert.Equal(50m, Convert.ToDecimal((await ProjectAsync(project))["percent_complete"]));

        await _engine.Documents.DeleteAsync(ProjectManagementApp.TaskDoc, open, Boss);
        Assert.Equal(100m, Convert.ToDecimal((await ProjectAsync(project))["percent_complete"]));
        Assert.NotNull(done);
    }

    [Fact]
    public async Task No_task_can_be_added_to_a_completed_or_cancelled_project()
    {
        var project = await NewProjectAsync("Old", ("status", "Completed"));
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => NewTaskAsync(project));
        Assert.Contains("completed", ex.Errors[0].Message);
    }

    [Fact]
    public async Task Time_logs_add_up_on_the_task_and_the_project_and_belong_to_the_person()
    {
        var project = await NewProjectAsync();
        var task = await NewTaskAsync(project);
        var log = await _engine.Documents.InsertAsync(ProjectManagementApp.TimeLogDoc, Values(("task", task), ("hours", 1.5), ("log_date", "2026-03-01")), Ann);
        await _engine.Documents.InsertAsync(ProjectManagementApp.TimeLogDoc, Values(("task", task), ("hours", 2), ("log_date", "2026-03-02")), Ann);

        Assert.Equal("ann@example.com", log["user"]);
        Assert.Equal(project, log["project"]);
        Assert.Equal(3.5, Convert.ToDouble((await TaskAsync(task))["logged_hours"]));
        Assert.Equal(3.5, Convert.ToDouble((await ProjectAsync(project))["logged_hours"]));

        await _engine.Documents.DeleteAsync(ProjectManagementApp.TimeLogDoc, (string)log["name"]!, Ann);
        Assert.Equal(2.0, Convert.ToDouble((await TaskAsync(task))["logged_hours"]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(-1)]
    public async Task Hours_must_be_between_zero_and_a_day(double hours)
    {
        var task = await NewTaskAsync(await NewProjectAsync());
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync(ProjectManagementApp.TimeLogDoc, Values(("task", task), ("hours", hours), ("log_date", "2026-03-01")), Ann));
        Assert.Contains(ex.Errors, e => e.Field == "hours");
    }

    [Fact]
    public async Task Only_a_project_manager_can_create_projects_and_outsiders_see_nothing()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            _engine.Documents.InsertAsync(ProjectManagementApp.ProjectDoc, Values(("project_name", "Nope")), Ann));
        var project = await NewProjectAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync(ProjectManagementApp.ProjectDoc, project, Outsider));
    }

    [Fact]
    public async Task The_summary_function_counts_tasks_by_status_and_overdue_work()
    {
        var project = await NewProjectAsync("Launch", ("budget_hours", 40));
        await NewTaskAsync(project, "A", ("due_date", "2020-01-01"));
        await NewTaskAsync(project, "B", ("status", "Done"), ("due_date", "2020-01-01"));
        await NewTaskAsync(project, "C", ("due_date", "2999-01-01"));

        var answer = await _engine.Methods.InvokeAsync("project_management.summary", Args(new { project }), Ann, false, default);
        var json = JsonSerializer.SerializeToElement(answer);
        Assert.Equal(3, json.GetProperty("tasks").GetInt32());
        Assert.Equal(1, json.GetProperty("overdue").GetInt32());
        Assert.Equal(2, json.GetProperty("byStatus").GetProperty("Open").GetInt32());
        Assert.Equal(33, json.GetProperty("percentComplete").GetDecimal());
    }

    [Fact]
    public async Task The_status_report_has_a_row_per_project()
    {
        var project = await NewProjectAsync("Launch", ("budget_hours", 10));
        await NewTaskAsync(project, "A", ("due_date", "2020-01-01"));

        var report = await _engine.ScriptReports.RunAsync("project_management.project_status", null, Ann);
        var row = Assert.Single(report.Rows);
        Assert.Equal(project, row["project"]);
        Assert.Equal(1L, row["tasks"]);
        Assert.Equal(1L, row["overdue"]);
    }
}
