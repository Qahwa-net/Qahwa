using System.Text.Json;
using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.ProjectManagement.Tests;

/// <summary>The example Server Scripts in Scripts/ (the ones the developer guide shows) do what the guide says.</summary>
public class ProjectManagementScriptTests : IAsyncLifetime
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Project User", UserContext.AllRole]);
    private static readonly UserContext Boss = new("boss@example.com", ["Project Manager", UserContext.AllRole]);

    public ProjectManagementScriptTests()
    {
        var app = new ProjectManagementApp();
        foreach (var file in Directory.GetFiles(app.DocTypesPath!, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            _engine.Registry.Add(DocTypeLoader.Parse(File.ReadAllText(file)));
        _engine.Registry.ValidateReferences();
        _engine.AppCatalog.Add(app);
    }

    public async Task InitializeAsync()
    {
        foreach (var person in new[] { Ann, Boss })
            await _engine.Documents.InsertAsync("User", Values(("email", person.User), ("full_name", person.User)), TestUsers.Admin);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Script(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scripts", file));

    private async Task InstallAsync(string file, params (string Key, object? Value)[] fields)
    {
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        await _engine.Documents.InsertAsync("Server Script", Values([("title", file), ("active", 1), .. fields, ("script", Script(file))]), TestUsers.Admin);
    }

    private async Task<string> ProjectAsync(string name = "Website") =>
        (string)(await _engine.Documents.InsertAsync(ProjectManagementApp.ProjectDoc, Values(("project_name", name)), Boss))["name"]!;

    private async Task<string> TaskAsync(string project, string subject, params (string Key, object? Value)[] more) =>
        (string)(await _engine.Documents.InsertAsync(ProjectManagementApp.TaskDoc, Values([("subject", subject), ("project", project), .. more]), Ann))["name"]!;

    private async Task<string> OverviewAsync(string project) =>
        (string)((await _engine.Documents.GetAsync(ProjectManagementApp.ProjectDoc, project, Boss))["task_overview"] ?? string.Empty);

    [Fact]
    public async Task The_project_shows_its_tasks_and_follows_every_change()
    {
        await InstallAsync("task_overview.server.js", ("reference_doctype", "Pm Task"), ("event", "After Save"));
        var project = await ProjectAsync();
        var design = await TaskAsync(project, "Design", ("assigned_to", "ann@example.com"), ("due_date", "2020-01-01"));
        await TaskAsync(project, "Content", ("due_date", "2999-01-01"));

        var overview = await OverviewAsync(project);
        Assert.Contains("[ ] ", overview);
        Assert.Contains("Design", overview);
        Assert.Contains("ann@example.com", overview);
        Assert.Contains("OVERDUE", overview);   // only the Design task, which is long past due
        Assert.Equal(1, overview.Split("OVERDUE").Length - 1);

        await _engine.Documents.UpdateAsync(ProjectManagementApp.TaskDoc, design, Values(("status", "Done")), Ann);
        overview = await OverviewAsync(project);
        Assert.Contains("[x] ", overview);
        Assert.DoesNotContain("OVERDUE", overview); // a finished task is not overdue
    }

    [Fact]
    public async Task A_task_moved_to_another_project_updates_both_boxes()
    {
        await InstallAsync("task_overview.server.js", ("reference_doctype", "Pm Task"), ("event", "After Save"));
        var first = await ProjectAsync("First");
        var second = await ProjectAsync("Second");
        var task = await TaskAsync(first, "Wanderer");
        Assert.Contains("Wanderer", await OverviewAsync(first));

        await _engine.Documents.UpdateAsync(ProjectManagementApp.TaskDoc, task, Values(("project", second)), Ann);
        Assert.Equal("No tasks yet.", await OverviewAsync(first));
        Assert.Contains("Wanderer", await OverviewAsync(second));
    }

    [Fact]
    public async Task The_daily_reminder_emails_the_person_of_each_unfinished_task_due_today()
    {
        await InstallAsync("task_due_reminders.server.js", ("event", "Scheduled"), ("frequency", "Daily"));
        Task<(int Ran, int Failed)> RunAsync() => _engine.ServerScripts.RunScheduledAsync((_, _) => Task.FromResult<IUserContext?>(TestUsers.Admin), default);
        await RunAsync(); // first sight: only scheduled

        var project = await ProjectAsync();
        var runDay = DateOnly.FromDateTime(_engine.Clock.GetUtcNow().UtcDateTime.AddHours(25));
        var today = runDay.ToString("yyyy-MM-dd");
        var tomorrow = runDay.AddDays(1).ToString("yyyy-MM-dd");
        await TaskAsync(project, "Due and open", ("assigned_to", "ann@example.com"), ("due_date", today));
        await TaskAsync(project, "Due and boss", ("assigned_to", "boss@example.com"), ("due_date", today), ("status", "In Progress"));
        await TaskAsync(project, "Due but done", ("assigned_to", "ann@example.com"), ("due_date", today), ("status", "Done"));
        await TaskAsync(project, "Due, nobody", ("due_date", today));
        await TaskAsync(project, "Due tomorrow", ("assigned_to", "ann@example.com"), ("due_date", tomorrow));

        _engine.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal((1, 0), await RunAsync());

        var queue = (await _engine.Documents.ListAsync("Email Queue", new ListRequest { Fields = ["recipient", "subject", "reference_doctype"], Limit = 50 }, TestUsers.Admin)).Data;
        Assert.Equal(["ann@example.com", "boss@example.com"], queue.Select(r => (string)r["recipient"]!).Order().ToArray());
        Assert.All(queue, r => Assert.StartsWith("Task due today: Due and ", (string)r["subject"]!));
        Assert.All(queue, r => Assert.Equal("Pm Task", r["reference_doctype"]));
    }

    [Fact]
    public async Task The_api_script_lists_the_overdue_tasks_of_a_project()
    {
        await InstallAsync("project_overdue.server.js", ("event", "API"), ("method_name", "project_overdue"));
        var project = await ProjectAsync();
        await TaskAsync(project, "Late", ("due_date", "2020-01-01"));
        await TaskAsync(project, "Late but done", ("due_date", "2020-01-01"), ("status", "Done"));
        await TaskAsync(project, "Later", ("due_date", "2999-01-01"));

        var json = await _engine.ServerScripts.CallMethodAsync("project_overdue", new Dictionary<string, object?> { ["project"] = project }, Ann, default);
        var answer = JsonDocument.Parse(json).RootElement;
        Assert.Equal(2, answer.GetProperty("open").GetInt32());
        Assert.Equal(1, answer.GetProperty("overdue").GetInt32());
        Assert.Equal("Late", answer.GetProperty("tasks")[0].GetProperty("subject").GetString());
    }

    [Fact]
    public async Task The_daily_limit_script_refuses_the_hour_that_passes_twelve()
    {
        await InstallAsync("time_log_daily_limit.server.js", ("reference_doctype", "Pm Time Log"), ("event", "Validate"));
        var task = await TaskAsync(await ProjectAsync(), "Long day");
        Task<Dictionary<string, object?>> Log(double hours) =>
            _engine.Documents.InsertAsync(ProjectManagementApp.TimeLogDoc, Values(("task", task), ("hours", hours), ("log_date", "2026-03-01")), Ann);

        await Log(8);
        await Log(4);   // exactly 12 is allowed
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => Log(0.5));
        Assert.Contains("The most is 12", ex.Errors[0].Message);
    }
}
